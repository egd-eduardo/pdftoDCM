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

> **One limitation worth knowing before you start.** A routing rule sends to one
> destination on the Routing page. The settings file itself supports several per rule, and a
> rule edited by hand to name several keeps them, but the page offers one, so two
> destinations for the same message means two rules. Everything else in this document is
> configured on the HL7 page in Settings, behind the protected settings password.

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

| What | Who provides it | Where it is configured |
| --- | --- | --- |
| A **profile**: the message, its fields and where each value comes from | The receiving system's conformance statement | A JSON file you write and then import on the Profiles page |
| A **destination**: a host, a port and the four application and facility names | The receiving system's administrator | The Destinations page |
| A **routing rule**: which destination gets which message | You, from local policy | The Routing page |

All three live behind the same password as the Destinations and Worklist pages, and in two
files you can copy to the next workstation: the settings file itself, and the profile files
beside it. Both paths are shown on the pages.

Nothing is sent until the switch at the top of the Routing page is on. A destination that
exists and is not yet trusted sits there doing nothing, which is what commissioning an
interface on a machine that also does real work requires.

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
5. **Import the profile and configure the destination in Settings**, as in sections 6 and
   7, leaving the announcement switch off. Press Check the configuration and Test the
   connection; both touch the network at most to open a socket.
6. **Only then turn the switch on**, and convert one real document while somebody at the
   receiving system is watching for it.

Where no test endpoint exists, there is a listener that answers in each of the ways a real
receiver can, including the unhelpful ones. It is a commissioning tool and is **not part of
what a site receives**, so ask whoever supplied the application for it; from a source
checkout it is:

```
dotnet run --project tools/PdfToDicom.Hl7TestServer -- --port 2575 --answer accept
```

`--answer` takes `accept`, `error`, `reject`, `silent`, `hangup`, `garbage` and
`wrong-control-id`, and refuses anything else rather than quietly accepting everything.
`--delay` makes it answer slowly, which is how a timeout is tested without unplugging
anything. It is a commissioning tool and is deliberately absent from anything
handed to a site.

---

## 6. Configuring a destination

Settings, HL7 page, Destinations. Add an entry per receiving system.

| Field | What to put in it |
| --- | --- |
| Name | How it appears in the log and the error messages. Use the receiving system's name |
| Host and port | From the receiving system's administrator. 2575 is the usual MLLP port |
| Sending application and facility | What **this** application calls itself, for MSH-3 and MSH-4 |
| Receiving application and facility | What the **receiver** calls itself, for MSH-5 and MSH-6 |
| Connection and acknowledgement timeouts | 10 and 30 seconds suit almost everything |
| Further attempts | See the warning below. Zero unless you have a reason |
| Accept only these profiles | Empty accepts any. A list restricts this destination, and a rule that sends it something else is reported rather than quietly skipped |

**Test the connection** opens a connection to the destination and closes it. It sends
nothing, deliberately: a test message from a settings dialog would put a document in a
patient's record to answer the question of whether a port is open. Send a real test message
from the command line, with `--test-data`, as in section 5.

The same destination from a script, for a scripted interface or for commissioning:

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
others. Ask before setting it. The command line does not retry at all, for the same reason:
from a script, trying again is a decision somebody takes deliberately rather than a default
they inherit.

---

## 7. Routing, and the switch that makes it live

Settings, HL7 page, Routing. Three things live here.

**The switch: "Announce a conversion to these systems".** Off until an interface has been
commissioned. While it is off, destinations and rules can be configured, checked and
connection tested, and nothing is sent. Turning it on is the moment the interface becomes
live, and it is the first thing to look at when asking why nothing is being sent.

**The order.** What happens if one half fails:

| Order | What it means | When to choose it |
| --- | --- | --- |
| Convert and announce independently | Both happen; neither can stop the other | The default and the careful answer. A record system that is down cannot stop a report reaching the archive |
| Announce only what the archive accepted | The message is sent after a successful transmission | A receiver whose message refers to the study, since an ORU carrying a Study Instance UID no archive holds points at nothing |
| Announce first, convert if the message landed | A failed message cancels the conversion | A receiver that will not file an image until an order exists. The only order in which an HL7 failure stops a report reaching the archive, which is why it has to be chosen rather than arrived at |

**The rules.** Each one has a name, a profile, a destination and a list of conditions, all of
which must hold. Rules are considered in order, which the Earlier and Later buttons change,
and the first rule to name a destination wins it, so a rule that matches everything belongs
last. Two overlapping rules can never mean two messages to the same destination: that would
be a duplicate in a patient's record rather than a tidiness problem.

A condition matches on one of these, and nothing else:

| Matches on | Holds |
| --- | --- |
| `messageType`, `triggerEvent` | From the rule's own profile, so `ORU` and `R01` |
| `modality` | The configured modality |
| `studyDescription`, `documentTitle` | As entered on the form |
| `hasAccessionNumber` | `yes` or `no` |
| `source` | `operator` for a conversion somebody started by hand |

A value matches exactly; `*` matches anything that is present; alternatives are separated by
commas, as `ORU,ADT`. An empty condition list matches every message.

**There is deliberately no patient identifier to match on.** Routing on one is not routing:
it is one patient's reports going somewhere different from everybody else's, which is a
mistake nobody should be able to configure.

**Check the configuration** reports everything that would stop a message being sent: a rule
naming a destination that no longer exists, a profile that is not installed, a destination
that is disabled, two destinations sharing an identifier, or the switch being on with nothing
enabled to send. The same check runs when the dialog is saved, and a configuration that is
both live and broken cannot be saved: correct it, or switch announcements off while you work
on it.

**What the operator sees.** When a conversion will announce itself, the confirmation that
reads the patient's identity back also names the record systems that are about to be told.
After the run, the result keeps the two halves apart: a report that did not reach the archive
is not where a clinician will look for it, and a message that did not reach a record system
is a report that is safely filed and that somebody has not been told about. The second turns
the status amber rather than red, because sending the document again would duplicate it in
the archive to fix a problem that is not there.

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
- **Several destinations in one rule, from the page.** The file supports it; the page offers
  one destination per rule. Two destinations for the same message means two rules.
- **Carrying the HL7 configuration with Export settings.** That transfer carries the
  archives and the worklist servers, and not these. Copy the settings file and the profile
  folder, whose paths both pages show.
- **FHIR.** The engine's internals are kept free of HL7 v2 vocabulary precisely so that a
  FHIR transport can be added beside MLLP rather than instead of it. Nothing of the sort
  ships today.
- **TLS.** A destination can be configured for it, and it has not been commissioned against
  a receiver that requires it. Where the link crosses anything untrusted, say so when asking
  for the endpoint and expect to test it carefully.

---

## 12. Verifying the engine itself

The engine has a verification of its own, run on every change by whoever builds the
application rather than at a site. It drives the command line front end against an MLLP
receiver written from the standard in the Python standard library, independently of this
application's own code, and checks 47 things: that the frame is one an unrelated reader can
find the edges of, that segments are separated by a carriage return alone, that an accented
name is encoded in the character set the message declares, that control ids are unique, and
that a refusal, a rejection, a silence, a hang up, unreadable bytes and an acknowledgement
quoting the wrong control id are each reported as themselves and none of them as success.

It is weaker evidence than the DICOM side's verification against DCMTK, which answers to an
independent and widely deployed implementation, and the script says so when it passes.
Commissioning a real interface still means sending to the real one.
