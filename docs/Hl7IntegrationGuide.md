# HL7 v2 Integration: Setup and Commissioning

**For hospital IT, the PACS administrator and the integration engineer.**

This document is how a new HL7 interface is configured for this application without anybody
writing code. Everything an interface needs, the message structure, the fields, where each
value comes from, the code translations, the character set and whether an acknowledgement is
expected, lives in a profile: one JSON file you can read, review and keep under change
control. The application ships no message built into it.

The companion document, `docs/Hl7IntegrationArchitecture.md`, explains why it is built this
way and what was decided against. You do not need it to configure an interface.

Current scope: **outbound ORU^R01 only**, over MLLP. Inbound messages and FHIR are
deliberately out of scope for now; section 11 says what that means for you.

> **Read this before anything else. What exists today is the engine, the profile model, the
> inspector in Settings and the two command line commands. There is no page yet on which a
> destination is saved, no routing configuration, and no switch that makes a conversion send
> a notification by itself.** Everything in sections 3 to 5 and 8 to 12 works now, from the
> command line and from the inspector, and is how a profile is written and commissioned.
> Sections 6 and 7 describe what is still to be built and say so in place. Until then an
> interface runs by calling `pdftodicom hl7-send` from a script, which is a real way to
> operate one and is not the same as a product feature.

---

## 1. What this interface does

When a report is converted and stored, this application can also tell another system that
the report exists. The usual recipient is a RIS, an EMR or an interface engine, and the
usual message is an ORU^R01 carrying the patient, the accession number and the Study
Instance UID, so the receiving system can link its own record to the study in the archive.

Two things it deliberately does **not** do:

- **It does not send the document itself inside the HL7 message.** The document goes to the
  archive as DICOM; the HL7 message is a notification that points at it. A receiver that
  wants the PDF retrieves it from the archive by the UID in the message.
- **It does not treat a successful connection as a successful delivery.** See section 8.

---

## 2. The three things an interface is made of

| What | Who provides it | Where it lives today |
| --- | --- | --- |
| A **profile**: the message, its fields and where each value comes from | The receiving system's conformance statement | A JSON file you keep under change control |
| A **destination**: a host, a port and the four application and facility names | The receiving system's administrator | Arguments to `hl7-send`. Not yet saved anywhere |
| A **routing rule**: which destination gets which message | You, from local policy | Not yet built. One profile and one destination per command |

The profile is the part that takes the work and the part that matters, and it is finished:
what you write today is what the application will read once the rest is in place. A
destination is four names, a host and a port, and retyping them in a script until there is a
page for them is tedious rather than difficult.

---

## 3. Writing a profile

Start from `samples/hl7-oru-r01.sample.json`. It is a working ORU^R01 with the study UID in
ZDS-1, which is the commonest shape, and every part of it is explained below. Copy it,
rename `profileId`, and change what the receiver's conformance statement tells you to
change.

### 3a. The header

```json
{
  "profileId": "stbartholomews-oru",
  "displayName": "St Bartholomew's RIS, report notification",
  "revision": 1,
  "enabled": true,

  "hl7Version": "2.4",
  "messageType": "ORU",
  "triggerEvent": "R01",
  "messageStructure": "ORU_R01",
  "processingId": "P"
}
```

- `profileId` is how the profile is referred to everywhere else, including in the
  transaction log. Keep it stable; change `revision` instead when you edit a profile, so a
  log entry from last month can be read against the profile that produced it.
- `processingId` is `P` for production, `T` for training and `D` for debugging. Several
  receivers route on this field alone, so sending `P` from a test workstation can put test
  data into a live record. Set it to `T` on a test machine and mean it.
- `enabled` set to false keeps a profile in place without using it, which is what you want
  while an interface is being commissioned or after it has been suspended.

### 3b. The encoding

```json
"encoding": {
  "fieldSeparator": "|",
  "encodingCharacters": "^~\\&",
  "characterSet": "8859/1",
  "onUnrepresentableCharacter": "refuse"
}
```

`characterSet` is an HL7 table 0211 value and is written into MSH-18. The recommended
setting for Portugal, Spain, France and most of western Europe is `8859/1`; for central
Europe `8859/2`; `UNICODE UTF-8` where the receiver genuinely supports it, which is worth
confirming rather than assuming, because a receiver that does not will show every accented
name as two wrong characters.

`onUnrepresentableCharacter` decides what happens when a name contains a character the
declared set cannot carry, for example Ł in `8859/2` territory sent under `8859/1`:

| Setting | What happens | When to use it |
| --- | --- | --- |
| `refuse` | The message is not sent and the reason names the character and the field | The default, and the right answer for a patient identifier or a name |
| `transliterate` | Accents are removed where that produces a representable character, and the substitution is written to the log | Only where the receiver's own display is already limited and the local policy accepts it |

There is no "send it anyway" setting. A name that arrives mangled is a name somebody may
fail to match to a patient, and this application will not produce one silently.

### 3c. The acknowledgement policy

```json
"acknowledgement": {
  "expect": "accept",
  "acceptAckType": "AL",
  "applicationAckType": "NE",
  "timeoutSeconds": 30
}
```

`expect` is the important one and it is not something to guess:

| `expect` | Meaning |
| --- | --- |
| `none` | The receiver answers nothing. Nothing is waited for, and every send is reported as unconfirmed, because that is what it is |
| `accept` | The receiver answers that it received the message. The commonest case |
| `application` | The receiver answers that it processed the message. Stronger, and slower, since the answer waits on its own database |

`acceptAckType` and `applicationAckType` go into MSH-15 and MSH-16, where `AL` means always
answer, `NE` means never, `ER` means only on error and `SU` means only on success. These
tell the receiver what you want; `expect` tells this application what to wait for. Set them
consistently: asking for `NE` and then waiting for an answer produces a timeout on every
message, and the application will report every one of them as unconfirmed, correctly and
uselessly.

### 3d. The segments

```json
"segments": [
  { "id": "MSH", "usage": "R", "min": 1, "max": 1 },
  { "id": "PID", "usage": "R", "min": 1, "max": 1, "group": "PATIENT" },
  { "id": "OBR", "usage": "R", "min": 1, "max": 1, "group": "ORDER_OBSERVATION" },
  { "id": "OBX", "usage": "R", "min": 1, "max": 1, "group": "OBSERVATION" },
  { "id": "ZDS", "usage": "O", "min": 0, "max": 1 }
]
```

`usage` takes the four values a conformance statement uses: `R` required, `O` optional, `C`
conditional and `X` not supported. A segment marked `X` that appears in a message is
reported, which is how a receiver's "do not send us PD1" is enforced rather than remembered.
`max` of 0 means unbounded. Z segments are ordinary here: name them and they work.

### 3e. The fields

```json
"fields": [
  { "path": "PID-3.1", "name": "Patient identifier", "dataType": "ST", "usage": "R", "maxLength": 20 },
  { "path": "PID-8", "name": "Administrative sex", "dataType": "IS", "usage": "O",
    "allowedValues": ["M", "F", "O", ""] }
]
```

This is the receiver's rulebook, transcribed. `path` is written the way a conformance
statement writes it: `PID-3.1`, `OBX[2]-5`, `PID-5(2).1.2` for the second repetition.
`maxLength` of 0 means no stated limit. `allowedValues` restricts a field to a list, and an
empty string in that list is how you say that empty is acceptable, which for a type 2 field
it usually is.

Transcribe the receiver's limits even where you believe nothing will ever exceed them. The
point of this section is that a message which would be rejected is caught here, on your
screen, rather than at two in the morning in somebody else's error queue.

### 3f. The mappings

This is where a value comes from:

```json
"mappings": [
  { "target": "MSH-7", "source": "now", "transform": "hl7DateTime" },
  { "target": "PID-3.1", "source": "patient.id", "required": true },
  { "target": "PID-5.1.1", "source": "patient.familyName", "transform": "truncate:30", "required": true },
  { "target": "PID-8", "source": "patient.sex", "table": "sex" },
  { "target": "OBR-25", "value": "F" }
]
```

- `source` names a piece of application data; `value` is a constant. Use one or the other.
- `transform` applies a named transform. Chain several with a semicolon: `trim;truncate:30`.
- `table` translates the value through one of the profile's own tables, after the transform.
- `required` means an empty result is a reason to refuse the whole message. It is separate
  from the field's `usage`, and the difference matters: a field the receiver requires may
  have no source here at all, which is exactly the misconfiguration worth catching before
  the first message goes out. Mark the handful of values without which the message is
  meaningless, usually the patient identifier, the accession number and the study UID.

### 3g. The values you can use as a source

| Name | What it holds |
| --- | --- |
| `now`, `today` | The moment the message is built |
| `patient.id` | The patient identifier |
| `patient.familyName`, `patient.givenName`, `patient.middleName` | The name, in parts |
| `patient.prefix`, `patient.suffix` | Dr, Jr and the like |
| `patient.dateOfBirth` | ISO 8601, so `hl7Date` turns it into `yyyyMMdd` |
| `patient.sex` | `Male`, `Female`, `Other` or `Unspecified`, for use with a table |
| `patient.sexCode` | The DICOM code, `M`, `F`, `O` or empty, for use without one |
| `study.accessionNumber`, `study.description`, `study.id` | The study |
| `study.dateTime` | ISO 8601 |
| `study.instanceUid`, `series.instanceUid`, `document.sopInstanceUid` | The UIDs, after conversion |
| `document.title`, `document.instanceCount`, `document.path` | The document |
| `series.description`, `referring.physician` | As recorded on the form |
| `destination.sendingApplication`, `destination.sendingFacility` | From the destination, for MSH-3 and MSH-4 |
| `destination.receivingApplication`, `destination.receivingFacility` | From the destination, for MSH-5 and MSH-6 |

A source that holds nothing produces an empty field. **Nothing is invented.** There is no
setting that fills a missing date of birth or an unknown sex with a plausible value, because
a plausible wrong value is worse than an empty field: an empty field is visibly empty, and
an invented one is not.

### 3h. The transforms

| Transform | Effect |
| --- | --- |
| `trim`, `upper`, `lower` | As named |
| `digitsOnly` | Keeps the digits, which is how a formatted identifier becomes a bare one |
| `truncate:30` | Shortens to 30 characters. Never lengthens |
| `padLeft:8:0`, `padRight:8:0` | Pads to a width with a character |
| `substring:0:4` | A part of the value |
| `hl7Date`, `hl7DateTime`, `hl7Time` | `yyyyMMdd`, `yyyyMMddHHmmss`, `HHmmss` |
| `escapeNewlines` | Turns line breaks into the HL7 escape sequence for them |
| `base64` | Encodes the value, for an ED field |

The set is closed on purpose. There is no transform that runs a script or an expression,
because a profile is a file that gets copied between hospitals and reviewed by people who
are not programmers, and a file that can execute code is not reviewable.

### 3i. The tables

```json
"tables": {
  "sex": { "Male": "M", "Female": "F", "Other": "O", "Unspecified": "" }
}
```

A table is a plain translation from what this application holds to what the receiver accepts.
Where a receiver uses its own codes for a procedure or a department, this is where they go,
and the profile is then the only place anybody has to look to see what maps to what.

---

## 4. Checking a profile before anything is sent

Settings, HL7 page, has an inspector: paste a message and a profile and it lays the message
out field by field against the profile, naming every mandatory field that is empty and
everything the profile objects to. It touches no network.

The same thing from a command line, which is what you want when the profile is in version
control and you would like a script to check it:

```
pdftodicom hl7-check --profile stbartholomews-oru.json --message example.hl7
```

It exits 0 when the message satisfies the profile and 2 when it does not. To see what a
profile would produce, without sending it:

```
pdftodicom hl7-send --profile stbartholomews-oru.json --test-data --no-send
```

`--test-data` fills the message with obviously invented values: the patient is named
ZZZTEST, so a message that escapes into a real system during commissioning is recognisable
as a test by whoever finds it. Override any of them with `--set`, which takes the source
names from section 3g:

```
pdftodicom hl7-send --profile p.json --test-data --no-send \
    --set patient.id=12345 --set patient.familyName=Gonçalves
```

Pipe that output back into `hl7-check` and you have verified the profile against itself,
which is a smaller claim than it sounds and still catches most transcription errors.

---

## 5. Commissioning against the real receiver

In order, because each step rules out the next step's commonest cause of confusion:

1. **Check the profile offline**, as in section 4. Do not start with a network.
2. **Send one test message** to the receiver's test endpoint:

   ```
   pdftodicom hl7-send --profile p.json --to ris-test.hospital.local:2575 \
       --timeout 10 --test-data
   ```

   Read the three lines it prints: the control id, the outcome and the explanation. Section
   8 says what each outcome means and what to do about it.
3. **Ask the receiving system's administrator to confirm what arrived**, in their own
   system, by the control id printed in step 2. This is the step people skip, and it is the
   only one that proves the message was understood rather than merely accepted. An
   acknowledgement means the receiver parsed the message; it does not mean anybody can find
   the patient.
4. **Send a message with a real accented name**, because character set problems are
   invisible until they are not. A name like Gonçalves or Sofía will show them.
5. **Only then configure the destination in Settings** and enable the profile.

Where no test endpoint exists, `tools/PdfToDicom.Hl7TestServer` is a listener you can run
on your own machine that answers in each of the ways a real receiver can, including the
unhelpful ones:

```
dotnet run --project tools/PdfToDicom.Hl7TestServer -- --port 2575 --answer accept
```

`--answer` takes `accept`, `error`, `reject`, `silent`, `hangup`, `garbage` and
`wrong-control-id`, and refuses anything else rather than quietly accepting everything.
`--delay` makes it answer slowly, which is how a timeout is tested without unplugging
anything. It is a commissioning tool and is deliberately absent from anything
handed to a site.

---

## 6. The destination (not yet a settings page)

**This section describes what is still to be built.** Today a destination is given to
`hl7-send` as `--to host:port`, and the four names are given as values, exactly as in
section 5. What follows is what each one is for, which is the same either way.

| Field | What to put in it |
| --- | --- |
| Name | How it appears in the log and the error messages. Use the receiving system's name |
| Host and port | From the receiving system's administrator. 2575 is the usual MLLP port |
| Sending application and facility | What **this** application calls itself, for MSH-3 and MSH-4 |
| Receiving application and facility | What the **receiver** calls itself, for MSH-5 and MSH-6 |
| Connect and receive timeouts | 10 and 30 seconds suit almost everything. `--timeout` sets the second one |
| Retries | See the warning below. The command line never retries |
| Profiles | Which profiles may be sent here |

From a script, the four names are values like any others:

```
pdftodicom hl7-send --profile p.json --to ris.hospital.local:2575 \
    --set destination.sendingApplication=PDFTODICOM \
    --set destination.sendingFacility=RADIOLOGY \
    --set destination.receivingApplication=RIS \
    --set destination.receivingFacility=HOSPITAL \
    --set patient.id=12345 --set study.accessionNumber=ACC001
```

The four names are not decoration. Many interface engines route on MSH-5 and reject anything
addressed to a name they do not recognise, and the commonest cause of a rejection on a first
attempt is one of these four being wrong or empty.

**About retries.** A retry after a timeout can produce a second copy of a document at the
far end, because a message that was received but not acknowledged looks exactly like one
that never arrived. The application always says which case it is in; see
`SentButUnconfirmed` in section 8. A retry count above 0 is a decision that duplicate
notifications are preferable to missing ones, which is true for some receivers and false for
others. Ask before setting it. The command line does not retry at all, for that reason: from
a script, trying again is a decision somebody takes deliberately rather than a default they
inherit.

---

## 7. Routing (not yet built)

**This section describes what is still to be built.** The engine has a routing component and
nothing configures it yet; today a command names one profile and one destination, which is
all the routing there is.

The intended behaviour, for when it is: a rule says which destination receives which
message, the first rule that names a destination wins it, so order matters and a rule that
matches everything belongs last, and a message that no rule matches is not sent, with the
log saying so rather than passing over it in silence.

---

## 8. What the outcomes mean

Every attempt ends in exactly one of these. They are what the command line prints, what the
log records and what the application shows.

| Outcome | What happened | What to do |
| --- | --- | --- |
| `Acknowledged` | Sent, and the receiver confirmed it by control id | Nothing. This is success |
| `Sent` | Sent, and the profile expects no acknowledgement | Nothing to do here, but note that nothing confirms it either. Prefer a profile that expects one |
| `Refused` | Not sent at all. The profile objected before a connection was opened | Fix the profile or the missing value. Nothing left this machine |
| `Rejected` | Sent, and the receiver answered that it will not accept it | Read the receiver's own complaint, which is reported back verbatim. Usually a field they require or a value their table does not hold |
| `Failed` | Nothing was sent. The connection failed, or the host refused it | A network, firewall or port problem. Nothing left this machine |
| `SentButUnconfirmed` | **The bytes were written and no usable answer came back.** The receiver may have it, or may not | Ask the receiving system whether the message arrived, by control id, before sending it again. Sending again may duplicate it |
| `AlreadyDelivered` | An identical message for this document was already acknowledged | Nothing. This is the duplicate guard working |

`SentButUnconfirmed` is the one that matters and the reason this engine exists in the shape
it does. A silent receiver, a receiver that hangs up, a reply that is not a message and a
reply quoting somebody else's control id all end here, and none of them is reported as
success. "The TCP write succeeded" is not evidence that a document reached a patient's
record, and this application will not claim otherwise.

---

## 9. The transaction log

Every attempt is written to a JSON lines file in the HL7 folder under the application's log
folder, which `pdftodicom config` will tell you. One line per attempt, holding the control
id, the destination, the profile and its revision, the outcome, the acknowledgement code,
the elapsed time and a SHA-256 hash of the message content.

**The hash and not the message.** The content of a notification includes a patient
identifier and a name, and a log that holds them is a second copy of clinical data in a
place nobody is guarding. The hash is enough to prove that two attempts carried the same
message, which is what a duplicate check and an audit both need.

Failures are recorded as carefully as successes, refusals included. A log that holds only
the successes is the one worth having and the one nobody keeps.

---

## 10. When it does not work

| Symptom | Where to look first |
| --- | --- |
| `Failed`, connection refused | Port, firewall, and whether the receiver is listening at all. `telnet host 2575` is enough to tell |
| `Rejected` naming a field | The field in section 3e. Their conformance statement and your transcription of it have diverged |
| `Rejected` with no detail | MSH-5 and MSH-6. An interface engine that does not recognise the name it is addressed by often rejects without saying why |
| `SentButUnconfirmed` every time | `expect` in section 3c. The receiver probably answers nothing, and the profile says it should |
| Accented names arrive wrong | `characterSet` in section 3b, and whether the receiver really supports what it claims |
| Nothing is sent and nothing is logged | The destination has no profile, the profile is disabled, or no routing rule matches |
| `Refused` for a field you can see a value for | The mapping's `source` name. A name that does not exist in section 3g holds nothing, silently, and that is a mistake worth making loud: run `hl7-check` and it will be named |

---

## 11. What is not supported, and what that means for you

- **Inbound messages.** This application sends; it does not receive. An ORM order will not
  populate a worklist here. Use the DICOM Modality Worklist for that, which is supported and
  documented in `docs/WorklistSetup.md`.
- **Message types other than ORU^R01.** The engine is not built around ORU, and the profile
  model carries the message type and structure as data, so another type is a profile and
  not a release. It has not been commissioned against a real receiver, so treat the first
  one as development rather than configuration.
- **FHIR.** The engine's internals are kept free of HL7 v2 vocabulary precisely so that a
  FHIR transport can be added beside MLLP rather than instead of it. Nothing of the sort
  ships today.
- **TLS.** A destination can be configured for it, and it has not been commissioned against
  a receiver that requires it. Where the link crosses anything untrusted, say so when asking
  for the endpoint and expect to test it carefully.

---

## 12. Verifying the engine itself

`build/verify-hl7-interop.sh` drives the command line front end against an MLLP receiver
written from the standard in the Python standard library, independently of this
application's own code, and checks 47 things: that the frame is one an unrelated reader can
find the edges of, that segments are separated by a carriage return alone, that an accented
name is encoded in the character set the message declares, that control ids are unique, and
that a refusal, a rejection, a silence, a hang up, unreadable bytes and an acknowledgement
quoting the wrong control id are each reported as themselves and none of them as success.

It is weaker evidence than the DICOM side's verification against DCMTK, which answers to an
independent and widely deployed implementation, and the script says so when it passes.
Commissioning a real interface still means sending to the real one.
