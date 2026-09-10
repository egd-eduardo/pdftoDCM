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
#   5. extracts the embedded PDF and confirms it is byte identical to the input.
#
# Requirements: the .NET SDK and DCMTK (dcmdump, storescp) on the PATH.
#   Debian/Ubuntu : sudo apt-get install dcmtk
#   macOS         : brew install dcmtk
#
# Usage: build/verify-against-dcmtk.sh [port]

set -euo pipefail

PORT="${1:-11112}"
AE_TITLE="TESTPACS"
CALLING_AE="PDF2DCM"

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
WORK_DIR="$(mktemp -d)"
RECEIVED_DIR="$WORK_DIR/received"
CONFIG_FILE="$WORK_DIR/config.json"
SAMPLE_PDF="$REPO_ROOT/samples/example-report.pdf"

SCP_PID=""
FAILURES=0

cleanup() {
    if [[ -n "$SCP_PID" ]] && kill -0 "$SCP_PID" 2>/dev/null; then
        kill "$SCP_PID" 2>/dev/null || true
        wait "$SCP_PID" 2>/dev/null || true
    fi

    rm -rf "$WORK_DIR"
}
trap cleanup EXIT

pass() { printf '  \033[32mPASS\033[0m  %s\n' "$1"; }
fail() { printf '  \033[31mFAIL\033[0m  %s\n' "$1"; FAILURES=$((FAILURES + 1)); }
step() { printf '\n\033[1m%s\033[0m\n' "$1"; }

# ------------------------------------------------------------------ Preconditions

step "Checking prerequisites"

for tool in dotnet dcmdump storescp dcm2pnm; do
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

RECEIVED="$(find "$RECEIVED_DIR" -type f | head -1)"

if [[ -n "$RECEIVED" ]]; then
    pass "the archive stored an object: $(basename "$RECEIVED")"
else
    fail "the archive did not store anything"
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
mapfile -t SC_OBJECTS < <(
    find "$RECEIVED_DIR" -type f | sort | while read -r f; do
        if dcmdump "$f" 2>/dev/null | grep -q "SecondaryCaptureImageStorage"; then
            echo "$f"
        fi
    done
)

if (( ${#SC_OBJECTS[@]} == 3 )); then
    pass "the archive stored three instances, one per page"
else
    fail "the archive stored ${#SC_OBJECTS[@]} image instance(s); three were expected"
fi

if (( ${#SC_OBJECTS[@]} > 0 )); then
    SERIES_COUNT=$(
        for f in "${SC_OBJECTS[@]}"; do
            dcmdump "$f" 2>/dev/null | grep "SeriesInstanceUID" | head -1
        done | sort -u | wc -l
    )

    if [[ "$SERIES_COUNT" == "1" ]]; then
        pass "all three pages share one Series Instance UID"
    else
        fail "the pages were spread across $SERIES_COUNT series; one was expected"
    fi

    STUDY_COUNT=$(
        for f in "${SC_OBJECTS[@]}"; do
            dcmdump "$f" 2>/dev/null | grep "StudyInstanceUID" | head -1
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

# ------------------------------------------------------------------ Result

step "Result"

if (( FAILURES == 0 )); then
    printf '  \033[32mAll interoperability checks passed.\033[0m\n\n'
    exit 0
fi

printf '  \033[31m%d check(s) failed.\033[0m\n\n' "$FAILURES"
exit 1
