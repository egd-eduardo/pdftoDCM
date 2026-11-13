# HL7 integration engine: architecture and implementation plan

**Status: proposed, for review. No code has been written.**

This document answers Phase 1 of the brief. It analyses the existing application, analyses
the supplied conformance statement, and proposes an architecture, a component
responsibility matrix, a configuration model, the data flows, the security controls and a
phased plan with its dependencies and risks.

Nothing here is built yet. Section 12 lists the questions whose answers change the design,
and section 11 says which of them block which phase.

---

## 1. The existing application, as it actually is

Measured from the repository rather than described from memory.

| Part | What it is | Size |
|---|---|---|
| `PdfToDicom.Core` | `net8.0` class library. All domain logic. No Windows coupling: three folder lookups aside, it runs unchanged on Linux, which is how the tests and the DCMTK interoperability checks execute in CI. | ~11 700 lines |
| `PdfToDicom.App` | `net8.0-windows`, WPF, MVVM without a toolkit. Cannot be built on Linux or macOS. | ~7 300 lines |
| `PdfToDicom.Cli` | `net8.0` console front end over the same Core. | ~1 800 lines |
| `PdfToDicom.Core.Tests` | xUnit, 291 tests, all against Core. | |
| `tools/PdfToDicom.TestWorklistServer` | A test server that must never reach a customer, excluded by name from the handover package. | |

Dependencies that matter: fo-dicom 5.2.2, PdfPig, PDFtoImage over PDFium and SkiaSharp,
SixLabors.ImageSharp, Serilog.

### Components the HL7 engine should reuse rather than reinvent

These are not loose similarities. Each is a shape the HL7 work needs and the repository
already has, tested and in service.

- **`PacsDestination`** already models exactly what an HL7 destination needs: identifier,
  name, host, port, enabled flag, timeout, retry count, retry delay, TLS, a certificate
  acceptance policy and the time it last worked. An `Hl7Destination` should be its sibling,
  not a reinvention, so that an administrator meets one mental model twice.
- **`IDicomNetworkService`** already defines transmission as an interface with a progress
  record and an ordered stage enumeration (`Connecting`, `Verifying`, `Storing`,
  `Retrying`, `Completed`), one result per destination, and the rule that a failure against
  one destination never prevents the others. The HL7 transport abstraction should mirror
  this, down to the progress reporting, because the user interface already knows how to
  display it.
- **`ConfigurationService` and `AppConfiguration`** give a single JSON configuration under
  `%APPDATA%\PdfToDicom\`, cloned through the serializer rather than property by property.
  The repository has already paid for the latter lesson once; HL7 configuration follows the
  same rule.
- **`SettingsTransfer`** already implements export and import of destinations and worklist
  servers, with a file marker, a parts selector and a merge or replace mode. Profile import
  and export is the same problem and should use the same mechanism.
- **`AuditLogger`** already writes a clinical audit trail with a fixed vocabulary of events.
  HL7 transactions extend that vocabulary; they do not need a second trail.
- **`ProtectedSettingsLock`** already guards the Destinations, Worklist and Backup pages
  behind a PBKDF2 verifier. The HL7 pages belong behind the same lock.
- **`tools/PdfToDicom.TestWorklistServer`** is the precedent for shipping a test endpoint in
  the repository and keeping it out of the package. An HL7 test endpoint follows it exactly.
- **`build/verify-against-dcmtk.sh`** is the precedent for proving interoperability against
  somebody else's implementation rather than against our own. The HL7 equivalent is section
  10.

### What must not change

The DICOM path is in clinical use. The plan below adds assemblies and configuration; it
changes no existing DICOM type, no existing interface and no existing configuration key.
`ConversionWorkflowService` gains an optional step, nothing more. A build with no HL7
profile configured must behave exactly as today, and that is a test, not an intention.

---

## 2. The conformance statement, as supplied

Biotronics3D, 3Dnet, publication HL7_0011, November 2018. Three profiles: ORU, ORM, ADT.

### 2.1 What it specifies

| Item | Value |
|---|---|
| HL7 version | 2.4 for ORU; versions 2.2, 2.3, 2.3.1 and 2.4 are supported; the ADT examples carry 2.3 |
| Profile type | Constrainable |
| Encoding | ER7, field separator `\|`, encoding characters `^~\&` |
| ORU interaction | ORU^R01, structure ORU_R01 |
| Acknowledgement | MSH-15 `NE`, MSH-16 `NE`, mode Immediate |
| Character set | A subset of the Windows ANSI code page, for example ISO 8859/1 |
| Segments not supported | PD1, NK1, NTE, PV2, CTD, FT1, CTI, DSC |
| ORC-1 Order Control | `CN` and `RE` only; for several procedures in one report, `CN` for all but the last and `RE` for the last |
| OBR-32 | Principal Result Interpreter, required, one or two occurrences: the report author, then the responsible or attending physician |
| OBX-2 Value Type | `TX`, `RP`, `ED` |
| OBX-5 | The report. `TX` is one line per segment, `FT` carries the whole report with `\.br\` for a newline, `RP` is a file location, `ED` carries an encapsulated binary object |
| OBX-11 | `P`, `F`, `C` |
| ORM | ORM^O001 with the order control and status matrix on page 17 |
| Z segment | `ZDS-1` carries the Study Instance UID |
| ADT | A04 registration, A08 update, A40 merge, all **received** by 3Dnet from the RIS |

### 2.2 Findings that change the design

These are the reason the engine must be generic rather than written to this document.

1. **This interface returns no acknowledgement for ORU.** `NE` in both MSH-15 and MSH-16
   means none is expected. The engine therefore cannot treat an acknowledgement as
   universal, and more importantly it must never report "accepted" when all that happened
   was a successful write to a socket. A profile declares what it expects, and the
   transaction record distinguishes *sent* from *acknowledged* from *accepted*. The brief
   asks for this and this interface is the proof that it is needed.

2. **UTF-8 is not supported, and this application uses it.** The DICOM objects this
   application produces use `ISO_IR 192` precisely so that an accented Portuguese name
   survives intact, and the interoperability suite tests exactly that. A name that is
   correct in the DICOM object may have no representation in ISO 8859/1. Silently
   transliterating a patient's name in one system and not the other is the class of fault
   this product exists to avoid. **Recommendation: a profile declares its character set, the
   mapping layer refuses to emit a value it cannot represent, and the operator is told,
   rather than a name being quietly mangled.** The alternative, configurable
   transliteration, should be a deliberate choice per destination and never the default.

3. **3Dnet creates a patient it does not know and never updates one it does.** A wrong
   demographic accepted once is not corrected by sending it again. This raises the cost of
   sending an HL7 message with unverified demographics, and argues for the same discipline
   the file name import already follows: reading fills the form, it never sends.

4. **Only one PID per ORU**, so a multi-patient batch is not expressible and the validator
   must refuse it rather than emit something the receiver will mis-handle.

5. **A PDF travels in OBX-5 as `ED`.** The worked example on page 15 shows
   `OBX|1|ED|...|^IM^TIFF^Base64^...`. The subtype for a PDF is not stated anywhere in the
   document and must be confirmed before Phase 4; `^AP^PDF^Base64` is the likely form but
   likely is not good enough here.

6. **The document contradicts itself on OBX-2.** The field table lists `TX`, `RP`, `ED` as
   supported; the note on OBX-5 describes the behaviour of `FT`. One of the two is wrong and
   the vendor must say which.

7. **`ZDS-1` carries the Study Instance UID**, which is the concrete bridge between the
   HL7 order and the DICOM object this application already generates. It is also a Z
   segment, so it is the first real test of whether the configuration model can express a
   custom segment without a code change. It should be used as that test deliberately.

8. **OBR-32 has no source in this application.** There is a Referring Physician field;
   there is no report author and no responsible physician. Either the profile supplies a
   configured static value, or the form grows a field. That is a product decision, not an
   engineering one.

9. **The transport is not specified anywhere in this document.** No MLLP, no port, no TLS,
   no connection policy. Only the encoding method, ER7, is given. **Phase 2 can be built
   against this gap but Phase 4 cannot be validated until Biotronics3D answer it.**

10. **The document is from 2018.** The deployed version of 3Dnet should be confirmed before
    its contents are treated as current.

### 2.3 What this does not tell us

The ADT profiles describe what 3Dnet receives from a RIS. Whether this application should
ever send or consume ADT is a scope question for section 12, not something the document
settles.

---

## 3. Proposed architecture

Three new assemblies. No HL7 type enters `PdfToDicom.Core`.

```
PdfToDicom.Core                     unchanged: DICOM, PDF, configuration, logging, audit
        ^                                  ^
        |                                  |
PdfToDicom.Interop                  protocol independent integration engine
    Abstractions/                   IIntegrationTransport, IMessageProfile,
                                    IMessageBuilder, IMessageParser,
                                    IMessageValidator, IRoutingEngine,
                                    ITransactionStore, IProfileStore
    Routing/                        rule evaluation, destination fan out
    Transactions/                   transaction record, duplicate guard, retry policy
    Mapping/                        source model, transforms, code translation tables
        ^
        |
PdfToDicom.Interop.Hl7V2            one protocol adapter among future others
    Encoding/                       ER7 reader and writer, escaping, character sets
    Model/                          Hl7Message, Segment, Field, Repetition, Component
    Profiles/                       profile schema, loader, validator, versioning
    Transport/                      MllpTransport, MllpTlsTransport
    Acknowledgement/                ACK parsing and interpretation
        ^
        |
PdfToDicom.App  /  PdfToDicom.Cli   user interface and command line

tools/PdfToDicom.Hl7TestServer      simulated endpoint, never packaged
```

**Why a separate assembly rather than folders inside Core.** Core is the DICOM domain and is
referenced by both front ends. A separate assembly makes it structurally impossible for HL7
work to break a DICOM type, lets the engine be referenced without the DICOM dependency, and
gives FHIR somewhere to live later that is not inside an HL7 v2 library. The cost is two
project references and two lines in the installer file list.

**Why the v2 adapter is separate from the engine.** The brief requires FHIR readiness, and
FHIR shares the destination model, the routing engine, the transaction store and the
mapping layer while sharing nothing at all with ER7 parsing or MLLP. The seam is drawn where
the reuse actually is.

### 3.1 Component responsibility matrix

| Component | Owns | Must not |
|---|---|---|
| `IIntegrationTransport` | Opening a connection, writing a framed message, reading a reply, timeouts, TLS | Know what a segment is |
| `MllpTransport` | MLLP framing, connection reuse, read and write timeouts | Parse or validate content |
| `Hl7Message` and the ER7 codec | Parsing and serialising any message from the encoding characters alone | Know any message type, segment or profile |
| `IMessageProfile` | The declared structure, cardinalities, data types, allowed values and mappings | Contain executable code |
| `IMessageBuilder` | Turning an application subject plus a profile into a message | Hard code a segment or a field |
| `IMessageValidator` | Checking a message against a profile, inbound or outbound | Repair a message |
| `IRoutingEngine` | Deciding which destinations receive a message | Send anything |
| `ITransactionStore` | The record of every attempt, its acknowledgement and its outcome | Decide policy |
| `Acknowledgement` | Parsing MSA and ERR, classifying AA, AE, AR, CA, CE, CR | Decide whether to retry |
| Retry policy | When and how often, duplicate protection | Touch a socket |
| `ConversionWorkflowService` | The order of DICOM and HL7 steps for a given plan | Build an HL7 message itself |

The rule behind the right hand column: every row that says "must not" is a place where a
vendor specific requirement would otherwise leak into the engine.

---

## 4. Configuration model

Profiles are JSON, validated against a schema on load, stored at
`%APPDATA%\PdfToDicom\Hl7\Profiles\`, with previous versions under `versions\` so that a
rollback is a file copy rather than an archaeology exercise.

A profile sketch, deliberately shown with real values from the supplied statement:

```json
{
  "profileId": "3dnet-oru-r01",
  "displayName": "3Dnet ORU^R01, report with encapsulated document",
  "revision": 1,
  "enabled": true,
  "hl7Version": "2.4",
  "messageType": "ORU",
  "triggerEvent": "R01",
  "messageStructure": "ORU_R01",
  "encoding": {
    "fieldSeparator": "|",
    "encodingCharacters": "^~\\&",
    "segmentTerminator": "\r",
    "characterSet": "8859/1",
    "onUnrepresentableCharacter": "refuse"
  },
  "processingId": "P",
  "controlId": { "format": "{prefix}{yyyyMMddHHmmss}{sequence:D5}", "prefix": "P2D" },
  "acknowledgement": {
    "expect": "none",
    "acceptAckType": "NE",
    "applicationAckType": "NE",
    "timeoutSeconds": 30
  },
  "segments": [
    { "id": "MSH", "usage": "R", "min": 1, "max": 1 },
    { "id": "PID", "usage": "R", "min": 1, "max": 1, "group": "PATIENT" },
    { "id": "PD1", "usage": "X" },
    { "id": "ORC", "usage": "O", "min": 0, "max": 1, "group": "ORDER_OBSERVATION" },
    { "id": "OBR", "usage": "R", "min": 1, "max": 1, "group": "ORDER_OBSERVATION" },
    { "id": "OBX", "usage": "O", "min": 0, "max": 1, "group": "OBSERVATION" },
    { "id": "ZDS", "usage": "O", "min": 0, "max": 1 }
  ],
  "fields": [
    { "path": "PID-3.1",  "name": "Patient identifier", "type": "ST", "usage": "R", "maxLength": 20 },
    { "path": "PID-5.1.1","name": "Family name",        "type": "ST", "usage": "R", "maxLength": 30 },
    { "path": "PID-8",    "name": "Administrative sex", "type": "IS", "usage": "O",
      "allowedValues": ["M", "F", "O", ""] },
    { "path": "ORC-1",    "name": "Order control",      "type": "ID", "usage": "R",
      "allowedValues": ["CN", "RE"] },
    { "path": "OBX-2",    "name": "Value type",         "type": "ID", "usage": "R",
      "allowedValues": ["TX", "RP", "ED"] },
    { "path": "ZDS-1",    "name": "Study Instance UID", "type": "ST", "usage": "O", "maxLength": 64 }
  ],
  "mappings": [
    { "target": "MSH-3.1",   "value": "PDFTODICOM" },
    { "target": "MSH-7",     "source": "now",                  "transform": "hl7DateTime" },
    { "target": "PID-3.1",   "source": "patient.id" },
    { "target": "PID-5.1.1", "source": "patient.familyName",   "transform": "truncate:30" },
    { "target": "PID-5.2",   "source": "patient.givenName",    "transform": "truncate:30" },
    { "target": "PID-7.1",   "source": "patient.dateOfBirth",  "transform": "hl7Date" },
    { "target": "PID-8",     "source": "patient.sex",          "table": "sex" },
    { "target": "OBR-3.1",   "source": "study.accessionNumber" },
    { "target": "OBR-25",    "value": "F" },
    { "target": "OBX-2",     "value": "ED" },
    { "target": "OBX-5",     "source": "document.bytes",       "transform": "encapsulated:AP:PDF:Base64" },
    { "target": "ZDS-1",     "source": "study.studyInstanceUid" }
  ],
  "tables": {
    "sex": { "Male": "M", "Female": "F", "Other": "O", "Unspecified": "" }
  }
}
```

Four deliberate decisions in that shape:

- **Structure and mapping are separate lists.** A vendor who changes a field's cardinality
  has not changed where the value comes from, and the reverse is also true.
- **`usage: "X"` expresses "not supported".** The supplied statement marks eight segments
  that way and the validator must refuse to emit them, which is only possible if the model
  can say it.
- **`transform` is a closed, named set, not an expression language.** A scripting engine
  inside clinical software is an injection surface and an unreviewable change path. A fixed
  vocabulary (`truncate`, `hl7Date`, `hl7DateTime`, `base64`, `encapsulated`, `pad`,
  `upper`, `substring`, table lookup) keeps "configurable without changing source code"
  honest while keeping what actually runs reviewable. Where a site needs a transform that
  does not exist, adding one is a small, tested change to a named list, not a new language.
- **`onUnrepresentableCharacter`** exists because of finding 2.2.2 and defaults to `refuse`.

Routing is a separate file, a list of ordered rules:

```json
[
  { "when": { "messageType": "ORU", "documentKind": "pdf" },
    "destinations": ["3dnet-main"], "profile": "3dnet-oru-r01", "stopOnMatch": false }
]
```

---

## 5. Data flow

**Outbound, with DICOM and HL7 linked:**

```
operator fills the form
        |
        v
ConversionWorkflowService ---> validation (existing, unchanged)
        |
        +--> DICOM object built and validated (existing, unchanged)
        |
        +--> routing engine: which destinations, which profile
                    |
                    v
            message builder (profile + subject)  --> validator --> preview
                    |
                    v
            transport adapter (MLLP) --> transaction record: sent
                    |
                    v
            acknowledgement, where the profile expects one --> accepted or rejected
        |
        v
workflow plan decides the order: DICOM first, HL7 first, in parallel, or independent
        |
        v
one combined status, with each leg reported separately and honestly
```

**Inbound:** a listener per configured endpoint, framed by the transport, parsed by the
codec, validated against the profiles that declare themselves inbound, routed to a handler,
acknowledged according to the profile. Nothing is acted upon before it validates.

---

## 6. Acknowledgement and transaction management

Every attempt produces a record: message control ID, destination, message type and trigger,
profile and revision, the time it was sent, the transmission status, the acknowledgement
status and code, any error detail, the attempt number, and the identifier of any related
transaction, including the DICOM SOP Instance UID when the two are linked.

Three states, never collapsed into one: **sent**, **acknowledged**, **accepted**. A profile
that expects no acknowledgement, as 3Dnet's ORU does, can only ever reach the first, and
the interface must say so rather than show a tick.

Duplicate protection keys on the content hash and the destination, so that a retry after an
uncertain outcome does not create a second document in the receiving system.

---

## 7. Security

- TLS per destination, modelled on `PacsDestination.UseTls`, with the same explicit choice
  about accepting a certificate that does not validate.
- No credential in a profile. Credentials follow the existing protected settings route.
- The HL7 configuration pages sit behind the existing protected settings lock.
- Every transaction is audited through the existing audit logger.
- **Log content is separated from transaction content.** The application log records
  identifiers and outcomes; full message bodies, which contain the report itself, go only to
  the transaction store, under a configurable retention policy. A report pasted into a log
  file that is then exported for support is a disclosure, and the existing log export
  feature makes that a real path.
- Inbound messages are untrusted input until they validate.

---

## 8. Testing

Mirroring what the DICOM side already does, because that is the standard this repository
has set:

- Unit tests for the codec against malformed, truncated and hostile input.
- Profile validation tests, including every "not supported" segment.
- A simulated MLLP endpoint under `tools/`, excluded from the package by the existing
  handover check, able to accept, reject, delay, return each acknowledgement code, and drop
  a connection mid message.
- An interoperability script in the shape of `verify-against-dcmtk.sh`, proving the
  generated message is accepted by something we did not write.
- A test mode producing synthetic messages with no real patient information.

---

## 9. Phases, dependencies and risks

| Phase | Deliverable | Depends on | Principal risk |
|---|---|---|---|
| 1 | This document | The conformance statement | Agreeing the wrong scope; mitigated by section 12 |
| 2 | Engine: codec, model, transport, transaction store | Phase 1 approved, **transport confirmed** | Building MLLP against an unconfirmed assumption |
| 3 | Profile model, loader, schema, versioning, mapping | Phase 2 | Over-general configuration that nobody can fill in |
| 4 | Destinations, routing, the 3Dnet profile | Phase 3, **vendor answers on ED subtype and OBX-2** | The character set clash, finding 2.2.2 |
| 5 | Editor, preview, validation interface | Phase 3 | WPF has no automated coverage; every window is a manual release step |
| 6 | Acknowledgements, logging, transactions in the interface | Phase 4 | Reporting success that was not success |
| 7 | Linking to the DICOM workflow | Phase 6 | Regressing the DICOM path; mitigated by the existing 291 tests and the DCMTK suite |
| 8 | Test endpoint, interoperability script, documentation, packaging | All | Shipping the test server, which the handover check already prevents |

Phases 2 and 3 can begin without the vendor. Phase 4 cannot be validated without them.

---

## 10. What this does not propose

- No change to any existing DICOM type, interface or configuration key.
- No FHIR. The architecture leaves room for it as a separate adapter and nothing more.
- No scripting language in configuration, for the reason given in section 4.
- No ADT handling until section 12 settles whether this application has a role in it.
- No version number change. That is the author's decision.

---

## 11. Open questions

Answers to these change the design rather than the schedule.

1. **Transport.** MLLP over TCP? Which port? TLS? The supplied document does not say, and
   Phase 4 cannot be validated without it.
2. **The ED subtype for a PDF in OBX-5.** The example shows a TIFF. What exactly does 3Dnet
   accept for a PDF?
3. **OBX-2.** The table says `TX`, `RP`, `ED`; the note describes `FT`. Which is correct?
4. **The character set clash.** The DICOM objects carry UTF-8 so that Portuguese names
   survive; this interface does not accept UTF-8. Refuse, transliterate, or decide per
   destination? The recommendation is to refuse and say so.
5. **OBR-32, the report author and the responsible physician.** A configured static value,
   or a new field on the form?
6. **Scope.** Is this application sending reports only, ORU, or is it also expected to send
   orders, ORM, and to consume ADT? The three have very different weights.
7. **The deployed 3Dnet version**, given the document is from 2018.
8. **Sending application and facility.** MSH-3 and MSH-4 values agreed with the site.

---

## 12. Recommendation

Approve the separation in section 3 and the configuration model in section 4, then let
Phases 2 and 3 proceed while questions 1 to 3 go to Biotronics3D. Question 4 should be
decided before Phase 4 begins, because it is the one question in this document with a
patient safety answer rather than an engineering one.
