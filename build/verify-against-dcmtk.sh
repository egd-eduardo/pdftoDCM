#!/usr/bin/env bash
#
# End to end interoperability verification against DCMTK, the reference DICOM toolkit.
#
# This is the check that answers the question a PACS administrator actually cares about:
# does an independent, standards conformant implementation accept what this application
# produces? Unit tests cannot answer that, because they exercise the same library that
# wrote the file.
#
# The script:
#   1. starts a DCMTK storescp acting as a Storage SCP,
#   2. drives the application's own command line front end to convert and send a PDF,
#   3. confirms the archive received the object,
#   4. dumps the received object with dcmdump and checks the mandatory attributes,
#   5. extracts the embedded PDF and confirms it is byte identical to the input,
#   6. starts a DCMTK wlmscpfs acting as a Modality Worklist SCP and confirms the
#      application's worklist client queries it correctly, accents and all.
#
# Requirements: the .NET SDK and DCMTK (dcmdump, storescp, dcm2pnm, wlmscpfs, dump2dcm)
# on the PATH.
#   Debian/Ubuntu : sudo apt-get install dcmtk
#   macOS         : brew install dcmtk
#
# Usage: build/verify-against-dcmtk.sh [port]

set -euo pipefail

PORT="${1:-11112}"
AE_TITLE="TESTPACS"
CALLING_AE="PDF2DCM"

# The worklist SCP needs a port of its own, since both listeners run at once.
WORKLIST_PORT="${2:-$((PORT + 1))}"
WORKLIST_AE="TESTWL"

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
WORK_DIR="$(mktemp -d)"
RECEIVED_DIR="$WORK_DIR/received"
CONFIG_FILE="$WORK_DIR/config.json"
SAMPLE_PDF="$REPO_ROOT/samples/example-report.pdf"

SCP_PID=""
WORKLIST_PID=""
FAILURES=0

cleanup() {
    local pid
    for pid in "$SCP_PID" "$WORKLIST_PID"; do
        if [[ -n "$pid" ]] && kill -0 "$pid" 2>/dev/null; then
            kill "$pid" 2>/dev/null || true
            wait "$pid" 2>/dev/null || true
        fi
    done

    rm -rf "$WORK_DIR"
}
trap cleanup EXIT

pass() { printf '  \033[32mPASS\033[0m  %s\n' "$1"; }
fail() { printf '  \033[31mFAIL\033[0m  %s\n' "$1"; FAILURES=$((FAILURES + 1)); }
step() { printf '\n\033[1m%s\033[0m\n' "$1"; }

# ------------------------------------------------------------------ Preconditions

step "Checking prerequisites"

for tool in dotnet dcmdump storescp dcm2pnm wlmscpfs dump2dcm; do
    if command -v "$tool" >/dev/null 2>&1; then
        pass "$tool is available"
    else
        fail "$tool is not on the PATH"
    fi
done

if [[ ! -f "$SAMPLE_PDF" ]]; then
    fail "the sample PDF was not found at $SAMPLE_PDF"
fi

if (( FAILURES > 0 )); then
    printf '\nPrerequisites are missing. Stopping.\n'
    exit 1
fi

# ------------------------------------------------------------------ Build

step "Building the command line front end"

dotnet build "$REPO_ROOT/src/PdfToDicom.Cli/PdfToDicom.Cli.csproj" -c Release --nologo -v q >/dev/null
CLI="$REPO_ROOT/src/PdfToDicom.Cli/bin/Release/net8.0/pdftodicom.dll"

if [[ -f "$CLI" ]]; then
    pass "the command line front end built successfully"
else
    fail "the command line front end was not produced at $CLI"
    exit 1
fi

# ------------------------------------------------------------------ Start the archive

step "Starting a DCMTK Storage SCP on port $PORT"

mkdir -p "$RECEIVED_DIR"
storescp -aet "$AE_TITLE" -od "$RECEIVED_DIR" --fork "$PORT" > "$WORK_DIR/storescp.log" 2>&1 &
SCP_PID=$!

# Give the listener a moment to bind before the first association is attempted.
sleep 2

if kill -0 "$SCP_PID" 2>/dev/null; then
    pass "the Storage SCP is listening as $AE_TITLE on port $PORT"
else
    fail "the Storage SCP did not start; see $WORK_DIR/storescp.log"
    exit 1
fi

# ------------------------------------------------------------------ Configure

cat > "$CONFIG_FILE" <<JSON
{
  "ConfigurationVersion": 1,
  "HospitalName": "Interoperability Test",
  "InstitutionName": "Interoperability Test",
  "DepartmentName": "Radiology",
  "StationName": "TESTRIG",
  "CallingAeTitle": "$CALLING_AE",
  "Destinations": [
    {
      "Id": "reference",
      "Name": "DCMTK reference",
      "CalledAeTitle": "$AE_TITLE",
      "Host": "127.0.0.1",
      "Port": $PORT,
      "IsEnabled": true,
      "VerifyBeforeSend": true,
      "TimeoutSeconds": 20,
      "RetryCount": 1,
      "RetryDelaySeconds": 1,
      "UseTls": false
    }
  ],
  "CharacterSet": "Utf8",
  "UidStrategy": "UuidDerived",
  "Modality": "OT",
  "ConversionType": "WSD",
  "LogFolder": "$WORK_DIR/logs",
  "OutputFolder": "$WORK_DIR/out",
  "AlwaysSaveDicomLocally": true,
  "Language": "en-US"
}
JSON

# ------------------------------------------------------------------ Verification

step "Verifying the association with C-ECHO"

if dotnet "$CLI" echo --config "$CONFIG_FILE" > "$WORK_DIR/echo.log" 2>&1; then
    pass "C-ECHO was accepted by the reference archive"
else
    fail "C-ECHO failed; see $WORK_DIR/echo.log"
    cat "$WORK_DIR/echo.log"
fi

# ------------------------------------------------------------------ Convert and send

# Returns the whole dump of a stored object as a string.
#
# Deliberately not "dcmdump file | grep -q pattern". grep -q stops reading the moment it
# matches, which closes the pipe and kills dcmdump with SIGPIPE; under "set -o pipefail"
# the pipeline then reports failure even though the pattern WAS found. Whether that
# happens depends on scheduling, so the check passes most of the time and fails
# occasionally for no visible reason. Measured at roughly fifteen failures in two hundred
# on this very script.
#
# Reading the dump into a variable removes the pipeline, and with it the race.
dump_of() {
    dcmdump "$1" 2>/dev/null || true
}

# True when a stored object declares the given SOP Class.
declares_sop_class() {
    local dump
    dump="$(dump_of "$1")"
    [[ "$dump" == *"$2"* ]]
}

# Reads one attribute from a stored object.
#
# Keyed on the tag number, not the keyword. dcmdump prints the keyword AFTER the value, as
# in "(0020,000d) UI [1.2.3] #  6, 1 StudyInstanceUID", so a pattern anchored on the
# keyword matches nothing and silently yields an empty string. Two empty strings then
# compare equal, which turns a broken read into a passing test.
attribute_of() {
    local file="$1" tag="$2" dump line

    dump="$(dump_of "$file")"
    line="$(grep -F "($tag)" <<<"$dump" | head -1 || true)"

    sed -n 's/.*\[\([^]]*\)\].*/\1/p' <<<"$line"
}

# Waits until the archive has finished storing the objects it has acknowledged.
#
# storescp forks a child per association and answers the C-STORE before that child has
# necessarily finished writing and closing the file. Reading the directory once, straight
# after the sender reports success, therefore sees anything from a complete set to a
# half written file, and the result changes from run to run. A conversion now sends
# several objects per document, which widens the window and made this reproducible.
#
# Polling until the expected number of files is present AND every one of them parses is
# the honest fix: a file still being written does not parse.
wait_for_stored_objects() {
    local expected="$1"
    local deadline=$((SECONDS + 30))

    while (( SECONDS < deadline )); do
        if (( $(find "$RECEIVED_DIR" -type f | wc -l) >= expected )); then
            local complete=yes

            while read -r stored; do
                dcmdump "$stored" > /dev/null 2>&1 || complete=no
            done < <(find "$RECEIVED_DIR" -type f)

            if [[ "$complete" == "yes" ]]; then
                return 0
            fi
        fi

        sleep 0.2
    done

    return 1
}

step "Converting and transmitting the sample document"

# An accented Portuguese name is used deliberately: it exercises the UTF-8 encoding path
# end to end, which is where character set defects show up in the field.
if dotnet "$CLI" send \
    --config "$CONFIG_FILE" \
    --pdf "$SAMPLE_PDF" \
    --patient-id "TEST000123" \
    --surname "Gonçalves Aparício" \
    --given-name "João" \
    --dob 1968-03-14 \
    --sex M \
    --accession "ACCTEST0001" \
    --referring-physician "Ana Ribeiro" \
    --study-description "Relatório de Radiologia" > "$WORK_DIR/send.log" 2>&1; then
    pass "the application reported a successful transmission"
else
    fail "the transmission failed; see below"
    cat "$WORK_DIR/send.log"
fi

# ------------------------------------------------------------------ Inspect the result

step "Inspecting the object the archive received"

if wait_for_stored_objects 3; then
    pass "the archive finished storing all three objects"
else
    fail "the archive did not finish storing three objects within 30 seconds"
fi

# A conversion now sends the encapsulated document AND one rendered image per page, so the
# document has to be picked out by its SOP Class. Taking the first file on disk would
# sometimes pick a page image and every check below would then be testing the wrong object.
RECEIVED=""
while read -r candidate; do
    if declares_sop_class "$candidate" "EncapsulatedPDFStorage"; then
        RECEIVED="$candidate"
        break
    fi
done < <(find "$RECEIVED_DIR" -type f | sort)

if [[ -n "$RECEIVED" ]]; then
    pass "the archive stored the encapsulated document: $(basename "$RECEIVED")"
else
    fail "the archive did not store an encapsulated PDF object"
    exit 1
fi

DUMP="$WORK_DIR/dump.txt"
if dcmdump "$RECEIVED" > "$DUMP" 2>&1; then
    pass "dcmdump parsed the received object without error"
else
    fail "dcmdump could not parse the received object"
    cat "$DUMP"
fi

check_attribute() {
    local label="$1" pattern="$2"
    if grep -q "$pattern" "$DUMP"; then
        pass "$label"
    else
        fail "$label"
    fi
}

check_attribute "SOP Class is Encapsulated PDF Storage"   "EncapsulatedPDFStorage"
check_attribute "MIME type is application/pdf"            "application/pdf"
check_attribute "Specific Character Set is UTF-8"         "ISO_IR 192"
check_attribute "Patient ID was preserved"                "TEST000123"
check_attribute "Accession Number was preserved"          "ACCTEST0001"
check_attribute "Patient's Birth Date was preserved"      "19680314"
check_attribute "Modality is present"                     "Modality"
check_attribute "Conversion Type is present"              "ConversionType"
check_attribute "Burned In Annotation is present"         "BurnedInAnnotation"
check_attribute "Document Title is present"               "DocumentTitle"
check_attribute "Encapsulated Document is present"        "EncapsulatedDocument"
check_attribute "Study Instance UID is present"           "StudyInstanceUID"
check_attribute "Series Instance UID is present"          "SeriesInstanceUID"
check_attribute "SOP Instance UID is present"             "SOPInstanceUID"

# The accented name must survive the round trip. grep is given the UTF-8 bytes directly.
if grep -q "Gonçalves Aparício" "$DUMP"; then
    pass "the accented patient name survived the round trip"
else
    fail "the accented patient name was corrupted in transit"
fi

# ------------------------------------------------------------------ Payload integrity

step "Confirming the embedded PDF is byte identical to the input"

python3 - "$RECEIVED" "$SAMPLE_PDF" <<'PYTHON'
import sys

received_path, original_path = sys.argv[1], sys.argv[2]
data = open(received_path, 'rb').read()
original = open(original_path, 'rb').read()

# Locate Encapsulated Document (0042,0011). In Explicit VR Little Endian an OB element is
# encoded as group, element, VR, two reserved bytes, then a 32 bit length.
marker = bytes([0x42, 0x00, 0x11, 0x00])
offset = data.find(marker)

if offset < 0:
    print("  \033[31mFAIL\033[0m  Encapsulated Document (0042,0011) was not found")
    sys.exit(1)

vr = data[offset + 4:offset + 6]
length = int.from_bytes(data[offset + 8:offset + 12], 'little')
payload = data[offset + 12:offset + 12 + length]

ok = True

def check(condition, message):
    global ok
    if condition:
        print(f"  \033[32mPASS\033[0m  {message}")
    else:
        print(f"  \033[31mFAIL\033[0m  {message}")
        ok = False

check(vr == b'OB', f"the element has VR OB (found {vr.decode('ascii', 'replace')})")
check(length % 2 == 0, f"the value has an even length ({length} bytes)")
check(payload[:5] == b'%PDF-', "the payload begins with a PDF header")
check(payload[:len(original)] == original, "the payload is byte identical to the source PDF")
check(len(payload) - len(original) in (0, 1), "at most one padding byte was appended")

sys.exit(0 if ok else 1)
PYTHON

PYTHON_STATUS=$?
if (( PYTHON_STATUS != 0 )); then
    FAILURES=$((FAILURES + 1))
fi

# ------------------------------------------------------------------ Images

step "Converting and transmitting an image, and checking its pixels survive"

# A three page greyscale TIFF with an odd number of pixels per page. Every part of that
# is deliberate: multi page exercises one series holding several instances, greyscale
# exercises MONOCHROME2, and an odd pixel count exercises the padding byte that DICOM
# requires because every value must occupy an even number of bytes.
IMAGE_DIR="$WORK_DIR/image"
mkdir -p "$IMAGE_DIR" "$WORK_DIR/image-received"

python3 - "$IMAGE_DIR" <<'PYTHON'
import struct, sys, zlib
from pathlib import Path

out = Path(sys.argv[1])
WIDTH, HEIGHT, PAGES = 33, 17, 3

def page_bytes(page):
    return bytes(((x + y + page * 11) * 5) & 0xFF
                 for y in range(HEIGHT) for x in range(WIDTH))

# A minimal baseline TIFF, written by hand so the fixture depends on nothing that the
# software under test also depends on. Uncompressed 8 bit greyscale, one strip per page.
def ifd(entries, next_offset):
    out = struct.pack('<H', len(entries))
    for tag, typ, count, value in entries:
        out += struct.pack('<HHI', tag, typ, count)
        out += struct.pack('<I', value) if typ != 3 else struct.pack('<HH', value, 0)
    return out + struct.pack('<I', next_offset)

pages = [page_bytes(p) for p in range(PAGES)]
header = struct.pack('<2sHI', b'II', 42, 8)

ENTRIES = 9
ifd_size = 2 + ENTRIES * 12 + 4
offset = len(header)
ifd_offsets, data_offsets = [], []
for p in range(PAGES):
    ifd_offsets.append(offset)
    offset += ifd_size
for p in range(PAGES):
    data_offsets.append(offset)
    offset += len(pages[p])

body = b''
for p in range(PAGES):
    nxt = ifd_offsets[p + 1] if p + 1 < PAGES else 0
    body += ifd([
        (256, 3, 1, WIDTH),          # ImageWidth
        (257, 3, 1, HEIGHT),         # ImageLength
        (258, 3, 1, 8),              # BitsPerSample
        (259, 3, 1, 1),              # Compression: none
        (262, 3, 1, 1),              # PhotometricInterpretation: BlackIsZero
        (273, 4, 1, data_offsets[p]),# StripOffsets
        (277, 3, 1, 1),              # SamplesPerPixel
        (279, 4, 1, len(pages[p])),  # StripByteCounts
        (278, 3, 1, HEIGHT),         # RowsPerStrip
    ], nxt)

(out / 'scan.tiff').write_bytes(header + body + b''.join(pages))
(out / 'expected.raw').write_bytes(b''.join(pages))
print(f"  built a {PAGES} page {WIDTH}x{HEIGHT} greyscale TIFF fixture")
PYTHON

if dotnet "$CLI" send \
    --config "$CONFIG_FILE" \
    --image "$IMAGE_DIR/scan.tiff" \
    --patient-id "TEST000123" \
    --surname "Gonçalves Aparício" \
    --given-name "João" \
    --dob 1968-03-14 \
    --sex M \
    --accession "ACCTEST0002" > "$WORK_DIR/send-image.log" 2>&1; then
    pass "the application reported a successful image transmission"
else
    fail "the image transmission failed; see below"
    cat "$WORK_DIR/send-image.log"
fi

# storescp names each stored file after the SOP Class it received, so the names cannot be
# predicted from here. Every file is asked what it is instead.
#
# Restricted to this send's own study. The document conversion earlier in this run also
# produces Secondary Capture objects, one per rendered page, and counting those here would
# make this check fail for a reason that has nothing to do with the image being tested.
if ! wait_for_stored_objects 6; then
    fail "the archive did not finish storing the image's three pages within 30 seconds"
fi

IMAGE_STUDY=""
mapfile -t SC_CANDIDATES < <(
    find "$RECEIVED_DIR" -type f | sort | while read -r f; do
        if declares_sop_class "$f" "SecondaryCaptureImageStorage"; then
            echo "$f"
        fi
    done
)

# The image's study is the one holding objects whose accession is this send's.
for f in "${SC_CANDIDATES[@]}"; do
    if [[ "$(attribute_of "$f" "0008,0050")" == "ACCTEST0002" ]]; then
        IMAGE_STUDY="$(attribute_of "$f" "0020,000d")"
        break
    fi
done

SC_OBJECTS=()
for f in "${SC_CANDIDATES[@]}"; do
    if [[ -n "$IMAGE_STUDY" && "$(attribute_of "$f" "0020,000d")" == "$IMAGE_STUDY" ]]; then
        SC_OBJECTS+=("$f")
    fi
done

if (( ${#SC_OBJECTS[@]} == 3 )); then
    pass "the archive stored three instances, one per page"
else
    fail "the archive stored ${#SC_OBJECTS[@]} image instance(s); three were expected"
fi

if (( ${#SC_OBJECTS[@]} > 0 )); then
    SERIES_COUNT=$(
        for f in "${SC_OBJECTS[@]}"; do
            attribute_of "$f" "0020,000e"
        done | sort -u | wc -l
    )

    if [[ "$SERIES_COUNT" == "1" ]]; then
        pass "all three pages share one Series Instance UID"
    else
        fail "the pages were spread across $SERIES_COUNT series; one was expected"
    fi

    STUDY_COUNT=$(
        for f in "${SC_OBJECTS[@]}"; do
            attribute_of "$f" "0020,000d"
        done | sort -u | wc -l
    )

    if [[ "$STUDY_COUNT" == "1" ]]; then
        pass "all three pages share one Study Instance UID"
    else
        fail "the pages were spread across $STUDY_COUNT studies; one was expected"
    fi

    step "Confirming the pixels survived the round trip"

    # dcm2pnm renders the stored object back to a portable greyscale bitmap, so the
    # comparison is against what a viewer would actually display rather than against the
    # bytes this software wrote.
    PIXEL_FAILURES=0
    for i in 0 1 2; do
        f="${SC_OBJECTS[$i]}"
        INSTANCE=$(dcmdump "$f" 2>/dev/null | grep "InstanceNumber" | grep -oE '\[[0-9]+\]' | tr -d '[]')

        if dcm2pnm "$f" "$WORK_DIR/page-$INSTANCE.pgm" > /dev/null 2>&1; then
            :
        else
            fail "dcm2pnm could not render instance $INSTANCE"
            PIXEL_FAILURES=$((PIXEL_FAILURES + 1))
        fi
    done

    if (( PIXEL_FAILURES == 0 )); then
        pass "every stored instance rendered without error"
    fi

    python3 - "$WORK_DIR" "$IMAGE_DIR/expected.raw" <<'PYTHON'
import sys
from pathlib import Path

work = Path(sys.argv[1])
expected = Path(sys.argv[2]).read_bytes()
WIDTH, HEIGHT = 33, 17
page_size = WIDTH * HEIGHT

def read_pnm(path):
    data = path.read_bytes()
    fields, i = [], 0
    while len(fields) < 4:
        while data[i:i + 1].isspace():
            i += 1
        if data[i:i + 1] == b'#':
            while data[i:i + 1] != b'\n':
                i += 1
            continue
        j = i
        while not data[j:j + 1].isspace():
            j += 1
        fields.append(data[i:j])
        i = j
    return int(fields[1]), int(fields[2]), data[i + 1:]

ok = True
for page in range(3):
    path = work / f"page-{page + 1}.pgm"
    if not path.exists():
        print(f"  \033[31mFAIL\033[0m  page {page + 1} was not rendered")
        ok = False
        continue

    width, height, payload = read_pnm(path)
    want = expected[page * page_size:(page + 1) * page_size]

    if (width, height) != (WIDTH, HEIGHT):
        print(f"  \033[31mFAIL\033[0m  page {page + 1} is {width}x{height}, expected {WIDTH}x{HEIGHT}")
        ok = False
    elif payload[:page_size] != want:
        differing = sum(1 for a, b in zip(payload[:page_size], want) if a != b)
        print(f"  \033[31mFAIL\033[0m  page {page + 1} differs from the source in {differing} pixel(s)")
        ok = False
    else:
        print(f"  \033[32mPASS\033[0m  page {page + 1} is pixel for pixel identical to the source")

sys.exit(0 if ok else 1)
PYTHON

    if (( $? != 0 )); then
        FAILURES=$((FAILURES + 1))
    fi
fi

# ------------------------------------- The page images that accompany the document

# Why this matters: many PACS viewers will only open a study that contains an image. An
# Encapsulated PDF is not an image SOP Class, so at such a site the viewer refuses to
# launch and the report cannot be read at all. The converter therefore also sends every
# page rendered as a Secondary Capture image, in its own series inside the same study.
step "Checking the rendered page images that accompany the document"

DOC_STUDY="$(attribute_of "$RECEIVED" "0020,000d")"
DOC_SERIES="$(attribute_of "$RECEIVED" "0020,000e")"

if [[ -n "$DOC_STUDY" && -n "$DOC_SERIES" ]]; then
    pass "the document's study and series identifiers were read"
else
    fail "the document's identifiers could not be read, so nothing below can be trusted"
fi

# Only the images belonging to the document's study. The image conversion tested earlier in
# this run put its own Secondary Capture objects in the same receiving folder.
PAGE_IMAGES=()
while read -r candidate; do
    declares_sop_class "$candidate" "SecondaryCaptureImageStorage" || continue
    [[ "$(attribute_of "$candidate" "0020,000d")" == "$DOC_STUDY" ]] || continue
    PAGE_IMAGES+=("$candidate")
done < <(find "$RECEIVED_DIR" -type f | sort)

# The sample report is two pages, so two images should have arrived with it.
if [[ ${#PAGE_IMAGES[@]} -eq 2 ]]; then
    pass "both pages of the document arrived as Secondary Capture images"
else
    fail "expected 2 page images, found ${#PAGE_IMAGES[@]}"
fi

IMAGE_SERIES=""
IMAGE_SERIES_COUNT=0
PAGE_NUMBERS=""

for image in "${PAGE_IMAGES[@]}"; do
    IMG_DUMP="$WORK_DIR/$(basename "$image").txt"
    dcmdump "$image" > "$IMG_DUMP" 2>&1

    study="$(attribute_of "$image" "0020,000d")"
    series="$(attribute_of "$image" "0020,000e")"
    instance="$(attribute_of "$image" "0020,0013")"

    # Same study as the document: this is what makes one viewer launch show both.
    if [[ "$study" == "$DOC_STUDY" ]]; then
        pass "page $instance is in the same study as the document"
    else
        fail "page $instance is in study '$study', not the document's '$DOC_STUDY'"
    fi

    # A different series from the document: this is what keeps them distinguishable.
    if [[ "$series" != "$DOC_SERIES" ]]; then
        pass "page $instance is in its own series, separate from the document"
    else
        fail "page $instance shares the document's series"
    fi

    if [[ -z "$IMAGE_SERIES" ]]; then
        IMAGE_SERIES="$series"
        IMAGE_SERIES_COUNT=1
    elif [[ "$series" != "$IMAGE_SERIES" ]]; then
        IMAGE_SERIES_COUNT=$((IMAGE_SERIES_COUNT + 1))
    fi

    PAGE_NUMBERS="$PAGE_NUMBERS $instance"
done

# All the pages belong together, in one series, so a viewer pages through the report.
if [[ $IMAGE_SERIES_COUNT -eq 1 ]]; then
    pass "all the page images share a single series"
else
    fail "the page images are spread across $IMAGE_SERIES_COUNT series"
fi

if [[ "$(echo $PAGE_NUMBERS | tr ' ' '\n' | sort -n | tr '\n' ' ')" == "1 2 " ]]; then
    pass "the pages are numbered 1 and 2, so a viewer shows them in order"
else
    fail "unexpected page numbering:$PAGE_NUMBERS"
fi

# The decisive check. A renderer that produced blank pages would satisfy everything above
# and be worse than useless: the viewer would launch and show nothing at all.
step "Confirming the rendered pages actually carry the report"

BLANK=0
INKED=0

for image in "${PAGE_IMAGES[@]}"; do
    png="$WORK_DIR/$(basename "$image").pnm"

    if ! dcm2pnm "$image" "$png" > /dev/null 2>&1; then
        fail "$(basename "$image") could not be rendered back to an image"
        continue
    fi

    # Count samples that are not near white. A rendered page of a report is mostly white
    # with dark text, so a page with no dark samples at all is blank.
    dark="$(python3 - "$png" <<'PY'
import sys
data = open(sys.argv[1], 'rb').read()
# Skip the PNM header: magic, dimensions, maxval, each whitespace separated.
fields, index = 0, 0
while fields < 4 and index < len(data):
    while index < len(data) and data[index:index + 1].isspace():
        index += 1
    if data[index:index + 1] == b'#':
        while index < len(data) and data[index] != 0x0A:
            index += 1
        continue
    while index < len(data) and not data[index:index + 1].isspace():
        index += 1
    fields += 1
index += 1
print(sum(1 for b in data[index:] if b < 128))
PY
)"

    if [[ "$dark" -gt 1000 ]]; then
        pass "$(basename "$image") carries the report: $dark dark samples"
        INKED=$((INKED + 1))
    else
        fail "$(basename "$image") appears blank: only $dark dark samples"
        BLANK=$((BLANK + 1))
    fi
done

if [[ $INKED -eq 2 && $BLANK -eq 0 ]]; then
    pass "every page a clinician would open is legible, not a placeholder"
fi

# ------------------------------------------------------------------ Modality Worklist

# The point of this section is the same as the rest of the script: an independent
# implementation, not our own test server. The application ships a test worklist server
# under tools/, and verifying the client against it would only prove the two agree with
# each other. DCMTK's wlmscpfs was written by someone else, from the standard, and is
# what makes a passing result mean something.

step "Starting a DCMTK Modality Worklist SCP on port $WORKLIST_PORT"

WORKLIST_DB="$WORK_DIR/worklist"
mkdir -p "$WORKLIST_DB/$WORKLIST_AE"

# wlmscpfs takes a read lock on this file before each query and will not answer without
# it. An empty file is all it wants; its absence produces a bare "Out of Resources" that
# says nothing about the cause.
touch "$WORKLIST_DB/$WORKLIST_AE/lockfile"

# Scheduled today, so the application's default date range (Today) finds it without the
# script having to reconfigure anything.
WORKLIST_TODAY="$(date -u +%Y%m%d)"

# A name with accents, and long enough that a Person Name cannot hold it: 73 characters
# against the 64 a single component group allows. Both properties are deliberate. The
# accents prove UTF-8 survives the round trip through an implementation that is not ours,
# and the length exercises the shortening rule on a real response rather than in a unit
# test.
WORKLIST_FAMILY="ALBUQUERQUE MONTENEGRO DOS SANTOS RODRIGUES"
WORKLIST_GIVEN="JOSÉ ANTÓNIO FRANCISCO XAVIER"
WORKLIST_STUDY_UID="1.2.826.0.1.3680043.9.6008.99.4242"

cat > "$WORK_DIR/worklist.dump" <<DUMP
(0008,0005) CS [ISO_IR 192]
(0008,0050) SH [ACC20260042]
(0008,0080) LO [Interoperability Test]
(0008,0090) PN [SOUSA^MANUEL]
(0010,0010) PN [$WORKLIST_FAMILY^$WORKLIST_GIVEN]
(0010,0020) LO [P0000042]
(0010,0030) DA [19621130]
(0010,0040) CS [F]
(0020,000d) UI [$WORKLIST_STUDY_UID]
(0032,1060) LO [Relatório de Ecografia]
(0040,1001) SH [RP0000042]
(0040,0100) SQ
  (fffe,e000) na
    (0008,0060) CS [OT]
    (0040,0001) AE [ANYSTATION]
    (0040,0002) DA [$WORKLIST_TODAY]
    (0040,0003) TM [100000]
    (0040,0006) PN [COSTA^RITA]
    (0040,0007) LO [Ecografia Abdominal]
    (0040,0009) SH [SPS0000042]
    (0040,0020) CS [SCHEDULED]
  (fffe,e00d) na
(fffe,e0dd) na
DUMP

if dump2dcm +te "$WORK_DIR/worklist.dump" "$WORKLIST_DB/$WORKLIST_AE/entry.wl" \
        > "$WORK_DIR/dump2dcm.log" 2>&1; then
    pass "the worklist entry was built with dump2dcm"
else
    fail "dump2dcm could not build the worklist entry; see $WORK_DIR/dump2dcm.log"
    cat "$WORK_DIR/dump2dcm.log"
fi

# -dfr because the entry above carries what this application queries rather than the
# complete set wlmscpfs would otherwise insist on, and -csk so the file's own ISO_IR 192
# is returned instead of being stripped, which is the whole point of the accent test.
wlmscpfs -dfp "$WORKLIST_DB" -dfr -csk --fork "$WORKLIST_PORT" \
    > "$WORK_DIR/wlmscpfs.log" 2>&1 &
WORKLIST_PID=$!

sleep 2

if kill -0 "$WORKLIST_PID" 2>/dev/null; then
    pass "the Worklist SCP is listening as $WORKLIST_AE on port $WORKLIST_PORT"
else
    fail "the Worklist SCP did not start; see $WORK_DIR/wlmscpfs.log"
    cat "$WORK_DIR/wlmscpfs.log"
fi

# Written out per query so each case can set its own date range and nothing has to be
# edited in place.
worklist_config() {
    local path="$1"
    local range="$2"
    local from="${3:-null}"
    local to="${4:-null}"

    cat > "$path" <<JSON
{
  "ConfigurationVersion": 1,
  "CallingAeTitle": "$CALLING_AE",
  "Destinations": [],
  "LogFolder": "$WORK_DIR/logs",
  "OutputFolder": "$WORK_DIR/out",
  "Worklist": {
    "IsEnabled": true,
    "CalledAeTitle": "$WORKLIST_AE",
    "Host": "127.0.0.1",
    "Port": $WORKLIST_PORT,
    "DateRange": "$range",
    "ExplicitFrom": $from,
    "ExplicitTo": $to,
    "TimeoutSeconds": 20,
    "MaximumResults": 500
  }
}
JSON
}

WORKLIST_CONFIG="$WORK_DIR/worklist-config.json"
worklist_config "$WORKLIST_CONFIG" "Today"

step "Querying the reference Worklist SCP"

WORKLIST_OUT="$WORK_DIR/worklist.out"

if dotnet "$CLI" worklist --config "$WORKLIST_CONFIG" > "$WORKLIST_OUT" 2>&1; then
    pass "the worklist query was answered"
else
    fail "the worklist query failed; see $WORKLIST_OUT"
    cat "$WORKLIST_OUT"
fi

WORKLIST_TEXT="$(cat "$WORKLIST_OUT")"

if [[ "$WORKLIST_TEXT" == *"1 scheduled procedure(s) found."* ]]; then
    pass "today's scheduled procedure was returned by the default date range"
else
    fail "the default date range did not return the entry scheduled for today"
fi

if [[ "$WORKLIST_TEXT" == *"P0000042"* ]]; then
    pass "the Patient ID came back intact"
else
    fail "the Patient ID is missing from the response"
fi

if [[ "$WORKLIST_TEXT" == *"ACC20260042"* ]]; then
    pass "the Accession Number came back intact"
else
    fail "the Accession Number is missing from the response"
fi

# The field that makes the document land beside the images rather than in a study of its
# own, so it is worth asserting rather than assuming.
if [[ "$WORKLIST_TEXT" == *"$WORKLIST_STUDY_UID"* ]]; then
    pass "the Study Instance UID from the order came back, so the document can join it"
else
    fail "the Study Instance UID is missing from the response"
fi

# The accent test. A client that negotiated the character set wrongly would return
# "JOSE ANTONIO" or a run of replacement characters, and both would pass a length check.
if [[ "$WORKLIST_TEXT" == *"JOSÉ ANTÓNIO"* && "$WORKLIST_TEXT" == *"$WORKLIST_FAMILY"* ]]; then
    pass "the accented name survived the round trip through an independent server"
else
    fail "the accented name did not come back intact"
fi

if [[ "$WORKLIST_TEXT" == *"SHORTENED  the name is 73 characters"* ]]; then
    pass "the over-length name was recognised and reported at its true length"
else
    fail "the over-length name was not reported as shortened"
fi

# The rule the department chose: the surname is what an archive indexes, so it survives
# whole and the given name is what gives way.
if [[ "$WORKLIST_TEXT" == *"would be sent as '$WORKLIST_FAMILY^JOSÉ ANTÓNIO'"* ]]; then
    pass "the surname was kept whole and the given name shortened at a word boundary"
else
    fail "the name was not shortened as the rule requires"
fi

step "Checking that the worklist match keys actually narrow the query"

# Runs one query and returns its whole output. Read into a variable rather than piped
# into "grep -q" for the reason set out above dump_of: grep -q stops at the first match,
# the producer dies of SIGPIPE, and under "set -o pipefail" the check then fails having
# found exactly what it was looking for.
worklist_query() {
    dotnet "$CLI" worklist "$@" 2>&1 || true
}

# A client that sent its filters as return keys rather than match keys would return the
# entry for every query below, and the four cases together catch that.
MATCHED="$(worklist_query --config "$WORKLIST_CONFIG" --patient-id P0000042)"

if [[ "$MATCHED" == *"1 scheduled procedure(s) found."* ]]; then
    pass "a matching Patient ID returns the entry"
else
    fail "a matching Patient ID did not return the entry"
fi

UNMATCHED="$(worklist_query --config "$WORKLIST_CONFIG" --patient-id NOSUCHPATIENT)"

if [[ "$UNMATCHED" == *"no scheduled procedures"* ]]; then
    pass "a Patient ID that matches nothing returns nothing"
else
    fail "a Patient ID that matches nothing still returned something"
fi

WILDCARD="$(worklist_query --config "$WORKLIST_CONFIG" --surname 'ALBUQUERQUE*')"

if [[ "$WILDCARD" == *"1 scheduled procedure(s) found."* ]]; then
    pass "a wildcard on the family name matches"
else
    fail "a wildcard on the family name did not match"
fi

# A range the entry cannot fall in. This is what proves the date is sent as a range match
# key and not merely echoed back.
worklist_config "$WORK_DIR/worklist-past.json" "Explicit" '"2020-01-01"' '"2020-01-31"'

PAST="$(worklist_query --config "$WORK_DIR/worklist-past.json")"

if [[ "$PAST" == *"no scheduled procedures"* ]]; then
    pass "a date range in the past returns nothing, so the range is really being matched"
else
    fail "a date range in the past still returned the entry scheduled for today"
fi

# ------------------------------------------------------------------ Filename import

# This section exists because the file name path is the one place where nobody looks at
# the patient details before the object is created. Everything here is therefore checked
# against what an independent archive actually received, not against what the application
# says it sent.

step "Importing a document whose identifiers are only in its file name"

INBOX="$WORK_DIR/inbox"
mkdir -p "$INBOX"

# The example the feature was built from: patient number, name, accession number.
IMPORT_NAME="9999999999_JohnDoe_TEST123451.pdf"
IMPORT_FILE="$INBOX/$IMPORT_NAME"
cp "$SAMPLE_PDF" "$IMPORT_FILE"

cat > "$WORK_DIR/import.json" <<JSON
{
  "ConfigurationVersion": 1,
  "HospitalName": "Interoperability Test",
  "InstitutionName": "Interoperability Test",
  "CallingAeTitle": "$CALLING_AE",
  "Destinations": [
    {
      "Id": "reference",
      "Name": "DCMTK reference",
      "CalledAeTitle": "$AE_TITLE",
      "Host": "127.0.0.1",
      "Port": $PORT,
      "IsEnabled": true,
      "VerifyBeforeSend": true,
      "TimeoutSeconds": 20,
      "RetryCount": 1,
      "RetryDelaySeconds": 1,
      "UseTls": false
    }
  ],
  "CharacterSet": "Utf8",
  "UidStrategy": "UuidDerived",
  "Modality": "OT",
  "ConversionType": "WSD",
  "LogFolder": "$WORK_DIR/logs",
  "OutputFolder": "$WORK_DIR/out-import",
  "AlwaysSaveDicomLocally": true,
  "Language": "en-US",
  "RenderPdfPagesAsImages": false,
  "FilenameImport": {
    "IsEnabled": true,
    "Patterns": [
      "{PatientId}_{Name}_{AccessionNumber}_{DateOfBirth}",
      "{PatientId}_{Name}_{AccessionNumber}"
    ],
    "UseFileDateAsStudyDate": true,
    "DefaultSex": "O",
    "SplitRunTogetherNames": true,
    "AfterImport": "MoveToSubfolder",
    "ImportedSubfolder": "Imported",
    "FailedSubfolder": "Failed"
  }
}
JSON

# The creation date as the operating system reports it, which is what the application
# reads. Birth time is not recorded by every filesystem; where it is missing, stat prints
# 0 and the study date is simply not compared, rather than the check being faked.
IMPORT_BIRTH="$(stat -c %W "$IMPORT_FILE" 2>/dev/null || echo 0)"

if [[ "$IMPORT_BIRTH" =~ ^[0-9]+$ ]] && (( IMPORT_BIRTH > 0 )); then
    EXPECTED_STUDY_DATE="$(date -d "@$IMPORT_BIRTH" +%Y%m%d 2>/dev/null || echo "")"
else
    EXPECTED_STUDY_DATE=""
fi

if dotnet "$CLI" send \
    --config "$WORK_DIR/import.json" \
    --pdf "$IMPORT_FILE" \
    --from-filename > "$WORK_DIR/import.log" 2>&1; then
    pass "the document was converted and transmitted with nothing but its file name"
else
    fail "the import failed; see below"
    cat "$WORK_DIR/import.log"
fi

# Found by its Patient ID rather than by counting, because this archive already holds the
# objects from every section above.
IMPORTED_OBJECT=""

import_deadline=$((SECONDS + 30))

while (( SECONDS < import_deadline )); do
    while read -r stored; do
        # Both conditions matter. The Patient ID separates this document from every other
        # section's, and the SOP Class picks the document itself out of the page images
        # that may accompany it.
        if [[ "$(attribute_of "$stored" "0010,0020")" == "9999999999" ]] \
            && declares_sop_class "$stored" "EncapsulatedPDFStorage"; then
            IMPORTED_OBJECT="$stored"
            break
        fi
    done < <(find "$RECEIVED_DIR" -type f)

    [[ -n "$IMPORTED_OBJECT" ]] && break
    sleep 0.2
done

if [[ -n "$IMPORTED_OBJECT" ]]; then
    pass "the archive received an Encapsulated PDF carrying the Patient ID from the file name"
else
    fail "no object with Patient ID 9999999999 reached the archive"
    cat "$WORK_DIR/import.log"
fi

if [[ -n "$IMPORTED_OBJECT" ]]; then
    IMPORT_DUMP="$(dump_of "$IMPORTED_OBJECT")"

    if [[ "$(attribute_of "$IMPORTED_OBJECT" "0008,0050")" == "TEST123451" ]]; then
        pass "the Accession Number was read from the file name"
    else
        fail "the Accession Number is '$(attribute_of "$IMPORTED_OBJECT" "0008,0050")', expected TEST123451"
    fi

    if [[ "$(attribute_of "$IMPORTED_OBJECT" "0010,0010")" == "Doe^John" ]]; then
        pass "JohnDoe became the Person Name Doe^John, family name first"
    else
        fail "the Patient Name is '$(attribute_of "$IMPORTED_OBJECT" "0010,0010")', expected Doe^John"
    fi

    if [[ "$(attribute_of "$IMPORTED_OBJECT" "0010,0040")" == "O" ]]; then
        pass "Patient Sex is O, which is what was asked for rather than a guess"
    else
        fail "Patient Sex is '$(attribute_of "$IMPORTED_OBJECT" "0010,0040")', expected O"
    fi

    # The point is that the attribute is PRESENT and EMPTY. Type 2 says an unknown value is
    # expressed that way; omitting the attribute would be non conformant, and inventing a
    # date would be worse than either.
    if grep -qE '\(0010,0030\).*(no value available|#   0)' <<<"$IMPORT_DUMP"; then
        pass "Patient Birth Date is present and empty, as Type 2 requires of an unknown value"
    else
        fail "Patient Birth Date is not present and empty: $(grep -F '(0010,0030)' <<<"$IMPORT_DUMP" || echo 'absent altogether')"
    fi

    IMPORT_STUDY_DATE="$(attribute_of "$IMPORTED_OBJECT" "0008,0020")"
    IMPORT_CONTENT_DATE="$(attribute_of "$IMPORTED_OBJECT" "0008,0023")"

    if [[ "$IMPORT_STUDY_DATE" =~ ^[0-9]{8}$ ]]; then
        pass "Study Date is a valid DA value ($IMPORT_STUDY_DATE)"
    else
        fail "Study Date is '$IMPORT_STUDY_DATE', which is not a valid DA value"
    fi

    if [[ -z "$EXPECTED_STUDY_DATE" ]]; then
        printf '  \033[33mSKIP\033[0m  %s\n' \
            "this filesystem records no creation time, so the study date cannot be compared"
    elif [[ "$IMPORT_STUDY_DATE" == "$EXPECTED_STUDY_DATE" ]]; then
        pass "Study Date is the file's own creation date, not the moment of conversion"
    else
        fail "Study Date is '$IMPORT_STUDY_DATE' but the file was created on '$EXPECTED_STUDY_DATE'"
    fi

    # Both dates are written, and from different clocks. A future reader needs to be able
    # to tell when the document was issued from when this object was made.
    if [[ "$IMPORT_CONTENT_DATE" =~ ^[0-9]{8}$ ]]; then
        pass "Content Date is recorded separately ($IMPORT_CONTENT_DATE), so neither fact is lost"
    else
        fail "Content Date is '$IMPORT_CONTENT_DATE', which is not a valid DA value"
    fi

    if declares_sop_class "$IMPORTED_OBJECT" "1.2.840.10008.5.1.4.1.1.104.1" \
        || declares_sop_class "$IMPORTED_OBJECT" "EncapsulatedPDFStorage"; then
        pass "the imported object is an Encapsulated PDF instance"
    else
        fail "the imported object does not declare the Encapsulated PDF SOP Class"
    fi
fi

step "Confirming the source file cannot be imported twice"

if [[ ! -f "$IMPORT_FILE" ]]; then
    pass "the file is no longer in the watched folder"
else
    fail "the file is still in the watched folder and would be processed again"
fi

if [[ -f "$INBOX/Imported/$IMPORT_NAME" ]]; then
    pass "it was moved into Imported/ rather than deleted"
else
    fail "the file is not in Imported/; it may have been deleted or left in place"
fi

if cmp -s "$SAMPLE_PDF" "$INBOX/Imported/$IMPORT_NAME"; then
    pass "the moved file is byte for byte the original document"
else
    fail "the file in Imported/ differs from the document that was handed over"
fi

step "Confirming a short patient number gets its leading zeros back"

# The failure this prevents: a sending system writes 77 where the hospital's record is
# 0000000077, the archive matches patients on the string, and the document opens a second
# record for the same person.
PADDED_NAME="77_JaneRoe_TEST777777.pdf"
PADDED_FILE="$INBOX/$PADDED_NAME"
cp "$SAMPLE_PDF" "$PADDED_FILE"

if dotnet "$CLI" send \
    --config "$WORK_DIR/import.json" \
    --pdf "$PADDED_FILE" \
    --from-filename > "$WORK_DIR/padded.log" 2>&1; then
    pass "the document was converted and transmitted"
else
    fail "the padded import failed; see below"
    cat "$WORK_DIR/padded.log"
fi

PADDED_OBJECT=""
padded_deadline=$((SECONDS + 30))

while (( SECONDS < padded_deadline )); do
    while read -r stored; do
        if [[ "$(attribute_of "$stored" "0008,0050")" == "TEST777777" ]] \
            && declares_sop_class "$stored" "EncapsulatedPDFStorage"; then
            PADDED_OBJECT="$stored"
            break
        fi
    done < <(find "$RECEIVED_DIR" -type f)

    [[ -n "$PADDED_OBJECT" ]] && break
    sleep 0.2
done

if [[ -z "$PADDED_OBJECT" ]]; then
    fail "no object with Accession Number TEST777777 reached the archive"
elif [[ "$(attribute_of "$PADDED_OBJECT" "0010,0020")" == "0000000077" ]]; then
    pass "the archive received Patient ID 0000000077, padded from the 77 in the file name"
else
    fail "the archive received Patient ID '$(attribute_of "$PADDED_OBJECT" "0010,0020")', expected 0000000077"
fi

step "Confirming a file name that matches nothing is refused rather than guessed at"

UNREADABLE="$INBOX/scan0001.pdf"
cp "$SAMPLE_PDF" "$UNREADABLE"

RECEIVED_BEFORE_REFUSAL="$(find "$RECEIVED_DIR" -type f | wc -l)"

if dotnet "$CLI" send \
    --config "$WORK_DIR/import.json" \
    --pdf "$UNREADABLE" \
    --from-filename > "$WORK_DIR/refused.log" 2>&1; then
    fail "a file name matching no pattern was converted anyway"
else
    pass "the conversion was refused"
fi

sleep 1

if (( $(find "$RECEIVED_DIR" -type f | wc -l) == RECEIVED_BEFORE_REFUSAL )); then
    pass "nothing reached the archive, so no object was filed against a guessed patient"
else
    fail "an object reached the archive despite the file name being unreadable"
fi

if [[ -f "$INBOX/Failed/scan0001.pdf" ]]; then
    pass "the refused file was moved to Failed/, where somebody will see it"
else
    fail "the refused file is not in Failed/; it would be silently retried or lost"
fi

# ------------------------------------------------------------------ Result

step "Result"

if (( FAILURES == 0 )); then
    printf '  \033[32mAll interoperability checks passed.\033[0m\n\n'
    exit 0
fi

printf '  \033[31m%d check(s) failed.\033[0m\n\n' "$FAILURES"
exit 1
