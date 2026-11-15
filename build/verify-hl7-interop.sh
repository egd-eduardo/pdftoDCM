#!/usr/bin/env bash
#
# End to end interoperability verification for the HL7 v2 integration engine.
#
# The DICOM side of this product has DCMTK to answer to: an independent, widely deployed
# implementation that either accepts what we produce or does not. HL7 has no equivalent
# that can be assumed present on a build agent, so the counterparty here is a small MLLP
# receiver written from the standard in the Python standard library, in
# build/hl7/mllp-receiver.py. That is weaker evidence than DCMTK and the script says so
# rather than implying otherwise.
#
# What it does establish, which the unit tests cannot:
#   1. the bytes on the wire are a frame an unrelated reader can find the edges of,
#   2. the segment separator really is a carriage return and nothing else,
#   3. an accented name is encoded in the character set the message declares,
#   4. the control id is unique per message and is what the acknowledgement must quote,
#   5. a refusal, a rejection, a silence, a hang up, a garbage reply and an
#      acknowledgement quoting the wrong control id are each reported as themselves and
#      none of them is reported as success,
#   6. a message the profile refuses is never transmitted at all,
#   7. every attempt is written down, including the ones that failed.
#
# Requirements: the .NET SDK and python3. No HL7 library, deliberately.
#
# Usage: build/verify-hl7-interop.sh [first port]

set -euo pipefail

PORT="${1:-32575}"

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
WORK_DIR="$(mktemp -d)"
RECEIVER="$REPO_ROOT/build/hl7/mllp-receiver.py"
PROFILE="$REPO_ROOT/samples/hl7-oru-r01.sample.json"
TRANSACTIONS="$WORK_DIR/transactions"

RECEIVER_PID=""
FAILURES=0
CHECKS=0

cleanup() {
    if [[ -n "$RECEIVER_PID" ]] && kill -0 "$RECEIVER_PID" 2>/dev/null; then
        kill -9 "$RECEIVER_PID" 2>/dev/null || true
        wait "$RECEIVER_PID" 2>/dev/null || true
    fi

    rm -rf "$WORK_DIR"
}
trap cleanup EXIT

pass() { printf '  \033[32mPASS\033[0m  %s\n' "$1"; CHECKS=$((CHECKS + 1)); }
fail() {
    printf '  \033[31mFAIL\033[0m  %s\n' "$1"
    FAILURES=$((FAILURES + 1))
    CHECKS=$((CHECKS + 1))
}
step() { printf '\n\033[1m%s\033[0m\n' "$1"; }

# Each case gets a port of its own. A receiver that is being deliberately unhelpful, by
# holding a connection open and answering nothing, is still holding it when the next case
# starts, and reusing the port would make the next case fail for the wrong reason.
NEXT_PORT="$PORT"

# Starts the receiver and waits for it to say it is listening, rather than sleeping for a
# guessed interval. A guessed interval is how an interoperability script becomes a
# coin toss on a loaded build agent.
start_receiver() {
    local answer="$1"
    local dump="$2"
    local extra="${3:-}"

    RECEIVER_PORT="$NEXT_PORT"
    NEXT_PORT=$((NEXT_PORT + 1))

    local log="$WORK_DIR/receiver-$answer-$RECEIVER_PORT.log"

    # shellcheck disable=SC2086
    python3 -I "$RECEIVER" --port "$RECEIVER_PORT" --answer "$answer" --dump "$dump" $extra \
        > "$log" 2>&1 &
    RECEIVER_PID=$!

    local waited=0

    while ! grep -q "listening on" "$log" 2>/dev/null; do
        if ! kill -0 "$RECEIVER_PID" 2>/dev/null; then
            fail "the receiver did not start; see $log"
            return 1
        fi

        sleep 0.2
        waited=$((waited + 1))

        if (( waited > 100 )); then
            fail "the receiver did not report itself listening; see $log"
            return 1
        fi
    done

    return 0
}

stop_receiver() {
    if [[ -n "$RECEIVER_PID" ]] && kill -0 "$RECEIVER_PID" 2>/dev/null; then
        kill -9 "$RECEIVER_PID" 2>/dev/null || true
        wait "$RECEIVER_PID" 2>/dev/null || true
    fi

    RECEIVER_PID=""
}

# Sends one message and leaves the output in SEND_OUTPUT and the exit code in SEND_STATUS.
send() {
    set +e
    SEND_OUTPUT="$(dotnet "$CLI" hl7-send --profile "$PROFILE" --to "127.0.0.1:$RECEIVER_PORT" \
        --timeout "${SEND_TIMEOUT:-6}" --test-data \
        --set destination.sendingApplication=PDFTODICOM \
        --set destination.sendingFacility=RADIOLOGY \
        --set destination.receivingApplication=PACS \
        --set destination.receivingFacility=HOSPITAL \
        "$@" 2>&1)"
    SEND_STATUS=$?
    set -e
}

outcome_is() {
    local expected="$1"

    if grep -q "outcome *: $expected" <<<"$SEND_OUTPUT"; then
        pass "the outcome is reported as $expected"
    else
        fail "the outcome should be $expected; the command said: $(tr '\n' ' ' <<<"$SEND_OUTPUT")"
    fi
}

status_is() {
    local expected="$1"

    if [[ "$SEND_STATUS" == "$expected" ]]; then
        pass "the exit code is $expected"
    else
        fail "the exit code should be $expected and was $SEND_STATUS"
    fi
}

# ------------------------------------------------------------------ Preconditions

step "Checking prerequisites"

for tool in dotnet python3; do
    if command -v "$tool" >/dev/null 2>&1; then
        pass "$tool is available"
    else
        fail "$tool is not on the PATH"
    fi
done

for file in "$RECEIVER" "$PROFILE"; do
    if [[ -f "$file" ]]; then
        pass "$(basename "$file") is present"
    else
        fail "$file was not found"
    fi
done

if (( FAILURES > 0 )); then
    printf '\nPrerequisites are missing. Stopping.\n'
    exit 1
fi

step "Building the command line front end"

dotnet build "$REPO_ROOT/src/PdfToDicom.Cli/PdfToDicom.Cli.csproj" -c Release --nologo -v q >/dev/null
CLI="$REPO_ROOT/src/PdfToDicom.Cli/bin/Release/net8.0/pdftodicom.dll"

if [[ -f "$CLI" ]]; then
    pass "the command line front end built successfully"
else
    fail "the command line front end was not produced at $CLI"
    exit 1
fi

# ------------------------------------------------------------------ The profile alone

step "Checking a message against the profile, without a network"

MESSAGE="$WORK_DIR/message.hl7"

set +e
dotnet "$CLI" hl7-send --profile "$PROFILE" --test-data --no-send > "$MESSAGE" 2>/dev/null
BUILD_STATUS=$?
set -e

if [[ "$BUILD_STATUS" == "0" && -s "$MESSAGE" ]]; then
    pass "a message was built from the profile and printed"
else
    fail "the profile did not produce a message"
fi

# The preview is only worth anything if it is the thing that would be sent, so it is fed
# straight back in. This also checks that the printed form is one the reader accepts.
set +e
CHECK_OUTPUT="$(dotnet "$CLI" hl7-check --profile "$PROFILE" --message "$MESSAGE" 2>&1)"
CHECK_STATUS=$?
set -e

if [[ "$CHECK_STATUS" == "0" ]]; then
    pass "the printed message satisfies the profile it was built from"
else
    fail "the printed message does not satisfy its own profile: $CHECK_OUTPUT"
fi

if grep -q "ZZZTEST" <<<"$CHECK_OUTPUT"; then
    pass "the invented patient is recognisable as a test in the listing"
else
    fail "the listing does not show the invented patient"
fi

# A composite field must be shown whole. Showing MSH-9 as its first component would read
# "ORU" and invite the conclusion that the trigger event is missing.
if grep -qE "MSH-9 +ORU\^R01\^ORU_R01" <<<"$CHECK_OUTPUT"; then
    pass "MSH-9 is shown whole, with the trigger event and the structure"
else
    fail "MSH-9 is not shown whole: $(grep MSH-9 <<<"$CHECK_OUTPUT" || true)"
fi

step "Checking that a message missing a mandatory field is refused"

MUTILATED="$WORK_DIR/no-patient-id.hl7"
python3 -I - "$MESSAGE" "$MUTILATED" <<'PY'
import sys

raw = open(sys.argv[1], "rb").read().decode("latin-1")
out = []

for segment in raw.split("\r"):
    if segment.startswith("PID"):
        parts = segment.split("|")
        parts[3] = ""          # PID-3, the patient identifier
        segment = "|".join(parts)

    out.append(segment)

open(sys.argv[2], "wb").write("\r".join(out).encode("latin-1"))
PY

set +e
MUTILATED_OUTPUT="$(dotnet "$CLI" hl7-check --profile "$PROFILE" --message "$MUTILATED" 2>&1)"
MUTILATED_STATUS=$?
set -e

if [[ "$MUTILATED_STATUS" == "2" ]]; then
    pass "a message without a patient identifier is reported as not sendable"
else
    fail "a message without a patient identifier was not refused (exit $MUTILATED_STATUS)"
fi

if grep -q "PID-3" <<<"$MUTILATED_OUTPUT"; then
    pass "the objection names the field that is missing"
else
    fail "the objection does not name PID-3: $MUTILATED_OUTPUT"
fi

# ------------------------------------------------------------------ The wire

step "Sending to a receiver that acknowledges, and examining the bytes"

DUMP="$WORK_DIR/accepted"

if start_receiver accept "$DUMP"; then
    send
    outcome_is "Acknowledged"
    status_is 0
    stop_receiver

    FRAME="$DUMP/frame-001.bin"

    if [[ -s "$FRAME" ]]; then
        pass "the receiver read one complete frame"
    else
        fail "the receiver did not read a frame"
    fi

    if [[ -s "$FRAME" ]]; then
        # The receiver strips the wrapper, so what is left must start at MSH and must be
        # separated by carriage returns alone. A stray line feed is the single commonest
        # cause of an interface engine rejecting an otherwise correct message.
        if head -c 3 "$FRAME" | grep -q "MSH"; then
            pass "the framed content begins at MSH"
        else
            fail "the framed content does not begin at MSH"
        fi

        if python3 -I -c '
import sys
sys.exit(0 if b"\n" in open(sys.argv[1], "rb").read() else 1)' "$FRAME"; then
            fail "the message contains a line feed; segments must be separated by \\r alone"
        else
            pass "no line feed appears anywhere in the message"
        fi

        SEGMENTS="$(python3 -I -c '
import sys
raw = open(sys.argv[1], "rb").read().decode("latin-1")
print(len([s for s in raw.split("\r") if s]))' "$FRAME")"

        if [[ "$SEGMENTS" -ge 5 ]]; then
            pass "the message carries $SEGMENTS segments"
        else
            fail "the message carries only $SEGMENTS segments"
        fi

        for expected in "P" "2.4" "8859/1"; do
            if python3 -I -c '
import sys
raw = open(sys.argv[1], "rb").read().decode("latin-1")
header = raw.split("\r")[0].split("|")
sys.exit(0 if sys.argv[2] in header else 1)' "$FRAME" "$expected"; then
                pass "the header declares $expected"
            else
                fail "the header does not declare $expected"
            fi
        done
    fi
fi

step "Sending an accented name and checking the declared character set is the one used"

DUMP="$WORK_DIR/accented"

if start_receiver accept "$DUMP"; then
    send --set patient.familyName="Gonçalves" --set patient.givenName="Sofía"
    outcome_is "Acknowledged"
    stop_receiver

    FRAME="$DUMP/frame-001.bin"

    # The profile declares 8859/1, so ç must be the single byte 0xE7. Two bytes would mean
    # UTF-8 went out under a header claiming Latin-1, which a receiver reads as mojibake
    # and a patient reads as somebody else's name.
    if [[ -s "$FRAME" ]] && python3 -I -c '
import sys
raw = open(sys.argv[1], "rb").read()
sys.exit(0 if b"Gon\xe7alves" in raw and b"Gon\xc3\xa7alves" not in raw else 1)' "$FRAME"; then
        pass "the accented name is encoded as declared, one byte per character"
    else
        fail "the accented name was not encoded in the declared character set"
    fi
fi

step "Checking that the message control id is unique per message"

DUMP="$WORK_DIR/twice"

if start_receiver accept "$DUMP" "--count 2"; then
    send
    FIRST="$(sed -n 's/^control id *: //p' <<<"$SEND_OUTPUT")"
    send
    SECOND="$(sed -n 's/^control id *: //p' <<<"$SEND_OUTPUT")"
    stop_receiver

    if [[ -n "$FIRST" && -n "$SECOND" && "$FIRST" != "$SECOND" ]]; then
        pass "two messages carry different control ids ($FIRST, $SECOND)"
    else
        fail "two messages carried the same control id ($FIRST, $SECOND)"
    fi
fi

# ------------------------------------------------------------------ Unhappy receivers

step "Sending to a receiver that returns an application error"

if start_receiver error "$WORK_DIR/error"; then
    send
    outcome_is "Rejected"
    status_is 3

    if grep -q "Required field missing" <<<"$SEND_OUTPUT"; then
        pass "the receiver's own complaint is reported back"
    else
        fail "the receiver's complaint was not reported: $(tr '\n' ' ' <<<"$SEND_OUTPUT")"
    fi

    stop_receiver
fi

step "Sending to a receiver that rejects the message outright"

if start_receiver reject "$WORK_DIR/reject"; then
    send
    outcome_is "Rejected"
    status_is 3
    stop_receiver
fi

step "Sending to a receiver that answers nothing"

if start_receiver silent "$WORK_DIR/silent"; then
    send
    outcome_is "SentButUnconfirmed"
    status_is 3

    if grep -qi "success\|delivered\|accepted" <<<"$SEND_OUTPUT"; then
        fail "silence was described in words that suggest success"
    else
        pass "silence is not described as success"
    fi

    stop_receiver
fi

step "Sending to a receiver that hangs up without answering"

if start_receiver hangup "$WORK_DIR/hangup"; then
    send
    status_is 3

    if grep -qE "outcome *: (SentButUnconfirmed|Failed)" <<<"$SEND_OUTPUT"; then
        pass "a hang up is reported as unconfirmed or failed, never as acknowledged"
    else
        fail "a hang up produced: $(tr '\n' ' ' <<<"$SEND_OUTPUT")"
    fi

    stop_receiver
fi

step "Sending to a receiver that answers with something that is not a message"

if start_receiver garbage "$WORK_DIR/garbage"; then
    send
    status_is 3

    if grep -qE "outcome *: (SentButUnconfirmed|Failed)" <<<"$SEND_OUTPUT"; then
        pass "an unreadable reply is not mistaken for an acknowledgement"
    else
        fail "an unreadable reply produced: $(tr '\n' ' ' <<<"$SEND_OUTPUT")"
    fi

    stop_receiver
fi

step "Sending to a receiver that acknowledges a message it was never sent"

if start_receiver wrongid "$WORK_DIR/wrongid"; then
    send
    status_is 3

    if grep -qE "outcome *: Acknowledged" <<<"$SEND_OUTPUT"; then
        fail "an acknowledgement quoting another message's control id was accepted"
    else
        pass "an acknowledgement quoting another control id is not accepted"
    fi

    stop_receiver
fi

step "Sending to a port with nothing listening"

RECEIVER_PORT=$((NEXT_PORT + 50))
send
outcome_is "Failed"
status_is 3

if grep -qi "refus\|connect" <<<"$SEND_OUTPUT"; then
    pass "the reason given names the connection"
else
    fail "the reason given does not explain the failure: $(tr '\n' ' ' <<<"$SEND_OUTPUT")"
fi

# ------------------------------------------------------------------ Refusals

step "Checking that a message the profile refuses is never transmitted"

REFUSING_PROFILE="$WORK_DIR/refusing.json"
python3 -I - "$PROFILE" "$REFUSING_PROFILE" <<'PY'
import json
import sys

profile = json.load(open(sys.argv[1]))

# A mandatory field whose source nothing fills. The engine must refuse rather than send a
# message with a hole in it, and must refuse before opening a connection.
profile["fields"].append(
    {"path": "PID-18.1", "name": "Account number", "dataType": "ST", "usage": "R"})
profile["mappings"].append(
    {"target": "PID-18.1", "source": "patient.accountNumber", "required": True})

json.dump(profile, open(sys.argv[2], "w"), indent=2)
PY

DUMP="$WORK_DIR/refused"

if start_receiver accept "$DUMP"; then
    set +e
    REFUSED_OUTPUT="$(dotnet "$CLI" hl7-send --profile "$REFUSING_PROFILE" \
        --to "127.0.0.1:$RECEIVER_PORT" --timeout 6 --test-data 2>&1)"
    REFUSED_STATUS=$?
    set -e
    stop_receiver

    if grep -q "outcome *: Refused" <<<"$REFUSED_OUTPUT"; then
        pass "the message is refused for the field that cannot be filled"
    else
        fail "the message was not refused: $(tr '\n' ' ' <<<"$REFUSED_OUTPUT")"
    fi

    if [[ "$REFUSED_STATUS" == "2" ]]; then
        pass "a refusal exits 2, which is a validation failure and not a network one"
    else
        fail "a refusal exited $REFUSED_STATUS"
    fi

    if [[ ! -e "$DUMP/frame-001.bin" ]]; then
        pass "nothing reached the receiver"
    else
        fail "a message the profile refused was transmitted anyway"
    fi
fi

step "Checking that a character the destination cannot carry is refused, not mangled"

DUMP="$WORK_DIR/unrepresentable"

if start_receiver accept "$DUMP"; then
    # Ł has no representation in ISO-8859-1 and the sample profile says to refuse rather
    # than transliterate. Sending "Lukasiewicz" for "Łukasiewicz" is the quiet kind of
    # wrong this product is built to avoid.
    send --set patient.familyName="Łukasiewicz"
    stop_receiver

    if grep -qE "outcome *: Refused" <<<"$SEND_OUTPUT"; then
        pass "a name the character set cannot carry is refused"
    else
        fail "an unrepresentable name produced: $(tr '\n' ' ' <<<"$SEND_OUTPUT")"
    fi

    if [[ ! -e "$DUMP/frame-001.bin" ]]; then
        pass "nothing was transmitted"
    else
        fail "a message with an unrepresentable character was transmitted"
    fi
fi

# ------------------------------------------------------------------ The record

step "Checking that every attempt was written down"

LOG_FOLDER="$(dotnet "$CLI" config 2>/dev/null | sed -n 's/^Log folder *: //p')/hl7"
LINES=0

if [[ -d "$LOG_FOLDER" ]]; then
    LINES="$(cat "$LOG_FOLDER"/*.jsonl 2>/dev/null | wc -l | tr -d ' ')"
fi

if (( LINES > 0 )); then
    pass "the transaction log holds $LINES entries"
else
    fail "no transaction log was written under $LOG_FOLDER"
fi

if [[ "$LINES" -gt 0 ]] && python3 -I -c '
import glob
import json
import sys

outcomes = set()

for name in glob.glob(sys.argv[1] + "/*.jsonl"):
    for line in open(name, encoding="utf-8"):
        line = line.strip()

        if line:
            outcomes.add(json.loads(line).get("DeliveryState", ""))

# A log that holds only the successes is the one worth having and the one nobody keeps.
sys.exit(0 if len(outcomes) > 1 else 1)' "$LOG_FOLDER"; then
    pass "the log holds failures as well as successes"
else
    fail "the log does not distinguish outcomes"
fi

# ------------------------------------------------------------------ Verdict

step "Summary"

printf '  %d checks, %d failures\n\n' "$CHECKS" "$FAILURES"

if (( FAILURES > 0 )); then
    printf '\033[31mThe HL7 interoperability verification failed.\033[0m\n'
    exit 1
fi

printf '\033[32mThe HL7 interoperability verification passed.\033[0m\n'
printf 'Note: the counterparty was build/hl7/mllp-receiver.py, not a hospital interface\n'
printf 'engine. Commissioning a real interface still means sending to the real one.\n'
