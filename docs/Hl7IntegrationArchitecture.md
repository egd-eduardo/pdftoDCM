# HL7 integration engine: architecture and implementation plan

**Status: approved in outline. Phase 2 under way.**

Decided so far, and recorded in the sections that follow: inbound is out of scope, and the
first and only message type is **ORU^R01 outbound**.

**Phases 2 and 3 are complete.** `src/PdfToDicom.Interop.Hl7V2` holds the message model, the ER7
codec, the character set rules, MLLP framing and the outbound transport.
`src/PdfToDicom.Interop` holds the transaction record, the content hash and the append only
store that answers whether a document has already reached a destination. 138 tests cover
the two. Neither assembly is referenced by the application or the command line tool, so what
is published and what the installer carries are unchanged.

Phase 3 added the profile model, its reader, the closed transform set, the message builder
and the message validator. A profile is a JSON document; nothing in the builder knows what
ORU means, which the tests check by building an ADT from the same code.

**Phase 4 is complete.** Destinations, the routing engine and the dispatcher that ties the
pieces together: build, check, refuse a duplicate, send with retries where retrying is safe,
and write down every attempt.

**Phase 5 is built and needs looking at on real Windows.** The inspector's logic is tested:
a message laid out against a profile, segment by segment and field by field, with the
missing mandatory fields singled out and two profiles comparable against the same message.
It is reached from a new HL7 tab in Settings, behind the protected settings lock, which the
author chose over a button on the main window.

That split is deliberate. The interface has no automated coverage at all, so everything
that can be decided below it is, and the tab binds to a finished answer rather than working
one out. The tab itself is the part nobody can test here.

This is also the moment the application first references the engine, so its assemblies are
now inside the published executable. The installer names only that executable, so its file
list is unchanged.

This document answers Phase 1 of the brief. It analyses the existing application, reads the
supplied conformance statement as a specimen rather than as a target, and proposes an
architecture, a component responsibility matrix, a configuration model, the data flows, the
security controls and a phased plan with its dependencies and risks.

The engine is generic by mandate. No integration is privileged, and the supplied statement
serves only as evidence of what a real one demands.

Section 11 lists the questions still open, and section 12 says which to answer first.

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

## 2. What a real conformance statement demands of the engine

The supplied Biotronics3D statement is read here as a **specimen, not a target**. It is not
the integration this work exists to serve, and treating it as one would produce exactly the
thing the brief forbids: an engine shaped around a single vendor.

Its value is as evidence. It is a real document describing a real product, and every
awkward thing in it is a capability the engine must already have before any site can be
configured by an administrator rather than by a developer. Read that way, it earns its
place in the design without constraining it.

### 2.1 Observed in the specimen, required of the engine

| Observed | Capability the engine must have |
|---|---|
| Acknowledgement declared `NE` in both MSH-15 and MSH-16, so nothing is returned for ORU | Acknowledgement expectation is a property of the profile, including the value *none*. Transactions keep **sent**, **acknowledged** and **accepted** distinct, and a profile expecting nothing can only ever reach the first |
| Character set confined to a Windows ANSI subset; UTF-8 explicitly refused | Character set per profile, with a declared policy for a value that cannot be represented in it |
| Eight segments marked not supported | Usage `X`, and a validator that refuses to emit them rather than trusting the builder |
| `ZDS-1` carrying a Study Instance UID | Custom and Z segments configurable like any other, and DICOM identifiers mappable into them |
| `ORC-1` restricted to two values, one of which depends on whether the procedure is the last in the report | Allowed value lists, and conditional values that can depend on position within a repeating group |
| `OBR-32` required, with no counterpart anywhere in this application | A profile can supply a configured value, and validation reports a mandatory field with no source **before** anything is sent |
| One PID per message | Cardinality enforced, and a batch of several patients refused rather than truncated |
| `OBX-5` carrying a report as `TX` lines, as `FT` with escape sequences, as an `RP` reference, or as `ED` Base64 | One field may carry a whole document under several encodings, so transforms include encapsulation and a field's size limit is expressible |
| One product supporting 2.2, 2.3, 2.3.1 and 2.4, with ADT examples at 2.3 while the ORU profile is 2.4 | HL7 version belongs to the profile, not to the application and not even to the destination |
| Transport not mentioned anywhere in the document | Transport is a per destination adapter choice with its own settings, because a statement may simply not say, and the engine cannot assume MLLP |
| The document contradicting itself on `OBX-2` | The preview must show exactly what would go on the wire, because the written statement is not always the authority and an administrator needs to see the real message |

Eleven capabilities, none of them vendor specific, all of them evidenced rather than
imagined. That is the whole use this document makes of the specimen.

### 2.2 Two findings that are product decisions, not engine decisions

These two are raised not because of the vendor but because they will recur at nearly every
site, and the answer should be settled once rather than per integration.

1. **The character set clash.** This application sends DICOM with `ISO_IR 192` precisely so
   that an accented Portuguese name survives, and the interoperability suite tests exactly
   that. A receiving system restricted to a single byte code page may have no
   representation for the same name. Sending a correct name to one system and a mangled one
   to another is the class of fault this product exists to avoid. **The proposal is to
   refuse and tell the operator, with transliteration available only as a deliberate per
   destination choice.** This is question 2 in section 11.

2. **Mandatory fields with no source.** The specimen requires a report author and a
   responsible physician; this application knows only a referring physician. Every
   integration will produce some version of this. The engine can supply a configured
   constant, and the validator can refuse before sending, but which of those is right is a
   product decision. This is question 3 in section 11.

### 2.3 Where the specimen goes

Into the tests. The worked profile in section 4 is derived from it so that the
configuration model is exercised against a document somebody really wrote, rather than
against one invented to fit the model. It lives beside the test fixtures and ships to
nobody.

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

### 3.2 One deviation from the diagram above, and why

The diagram puts a protocol independent transport abstraction in `PdfToDicom.Interop` and
the MLLP adapter below it in the HL7 assembly. As built, the transport interface lives in
the HL7 assembly beside its one implementation, and `PdfToDicom.Interop` holds only the
transaction store.

The reason is that an interface with one implementation and one consumer describes nothing.
It cannot be shown to be the right shape, because nothing has yet disagreed with it, and the
second protocol would almost certainly want something different, at which point the
interface is changed rather than reused and the only thing achieved was the extra file.

What the architecture actually required is intact: the transaction store, which is the part
that genuinely has nothing to do with HL7, is in the protocol independent assembly and
references nothing. The abstraction is extracted when a second protocol exists to extract it
from, which is also the moment it can be checked against something.

### 3.1 Component responsibility matrix

| Component | Owns | Must not |
|---|---|---|
| `IIntegrationTransport` | Opening a connection, writing a framed message, reading a reply, timeouts, TLS | Know what a segment is |
| `MllpTransport` | MLLP framing, connection reuse, read and write timeouts | Parse or validate content |
| `Hl7Message` and the ER7 codec | Parsing and serialising any message from the encoding characters alone | Know any message type, segment or profile |
| `IMessageProfile` | The declared structure, cardinalities, data types, allowed values and mappings | Contain executable code |
| `IMessageBuilder` | Turning an application subject plus a profile into a message | Hard code a segment or a field |
| `IMessageValidator` | Checking a message against a profile, whether built here or parsed from a sample | Repair a message |
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

A profile sketch. The values are taken from the supplied specimen rather than invented,
so that the model is shown carrying something a real vendor really wrote:

```json
{
  "profileId": "example-oru-r01",
  "displayName": "Example ORU^R01, report with encapsulated document",
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
    "destinations": ["report-archive"], "profile": "example-oru-r01", "stopOnMatch": false }
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

**Inbound is out of scope.** This application sends; it does not listen. No port is opened,
no profile is marked inbound, and nothing arrives unasked. The decision is recorded in
section 10.

The seam survives the decision at no cost, because two things that are needed anyway
already do most of the work. The codec parses as readily as it writes, which is what reads
an acknowledgement and what lets the message inspector import and parse a sample message
somebody sends you. The validator already checks a parsed message against a profile. What
an inbound capability would add later is a listener, a profile direction flag and a
handler, and none of the three changes anything built before them.

---

## 6. Acknowledgement and transaction management

Every attempt produces a record: message control ID, destination, message type and trigger,
profile and revision, the time it was sent, the transmission status, the acknowledgement
status and code, any error detail, the attempt number, and the identifier of any related
transaction, including the DICOM SOP Instance UID when the two are linked.

Three states, never collapsed into one: **sent**, **acknowledged**, **accepted**. A profile
that expects no acknowledgement, which the specimen in section 2 is a real case of, can
only ever reach the first, and
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
- Anything parsed rather than built here is untrusted input until it validates. With
  inbound out of scope that means acknowledgements and the sample messages an administrator
  imports into the inspector, both of which arrive from outside and neither of which may be
  acted upon before it parses cleanly.

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

The brief lists inbound message testing. It is not in this plan, because inbound is not in
scope. The simulated endpoint still exists and still matters: it is what proves the sending
side behaves when a receiver rejects, stalls, or disappears mid message.

---

## 9. Phases, dependencies and risks

No phase depends on a third party. The specimen profile is a fixture, so Phase 4 can be
completed and proven without anyone outside this project.

| Phase | Deliverable | Depends on | Principal risk |
|---|---|---|---|
| 1 | This document | Nothing | Agreeing the wrong scope; mitigated by section 11 |
| 2 | Engine: codec, model, transport abstraction, outbound MLLP adapter, transaction store | Phase 1 approved | Building transport assumptions into the engine; mitigated by the adapter seam. Smaller than first proposed, since no listener is built |
| 3 | Profile model, loader, schema, versioning, mapping, transforms | Phase 2 | Configuration so general that nobody can fill it in; mitigated by filling it in with the specimen |
| 4 | Destinations, routing, and the worked profile exercising all eleven capabilities of section 2.1 | Phase 3 | A capability that turns out not to be expressible, which is precisely what this phase is for finding |
| 5 | Message editor, preview, validation interface | Phase 3 | WPF has no automated coverage, so every window is a manual release step |
| 6 | Acknowledgements, transaction log, interface reporting | Phase 4 | Reporting a success that was not one |
| 7 | Linking to the DICOM workflow, with independence preserved | Phase 6 | Regressing the DICOM path; mitigated by the 291 tests and the DCMTK suite |
| 8 | Test endpoint, interoperability script, documentation, packaging | All | Shipping the test endpoint, which the existing handover check already prevents |

The honest risk across all of it is Phase 5. The engine is testable on a build agent and
the interface is not, and an integration engine whose configuration cannot be driven
comfortably by an administrator has failed at its one stated purpose.

---

## 10. What this does not propose

- No change to any existing DICOM type, interface or configuration key.
- No FHIR. The architecture leaves room for it as a separate adapter and nothing more.
- No scripting language in configuration, for the reason given in section 4.
- **No message type but ORU^R01. Decided: reports outbound only.** Orders, `ORM`, and
  patient administration, `ADT`, are each a different weight of work and a different
  conversation with a site, and neither is built. The profile model does not privilege ORU,
  so adding one later is configuration plus whatever mapping sources it needs, not a
  redesign.
- **No listener, and therefore no inbound messages. Decided: inbound is out of scope.**
  A desktop application that accepts connections is a materially different product with a
  materially different security posture, and declining it removes a listening port, a
  service that would have to run when nobody is logged in, and the security review that
  would have come with both. The architecture keeps the seam, as section 5 describes, so
  the decision can be revisited without a redesign.
- No version number change. That is the author's decision.

---

## 11. Open questions

Three remain. All are the author's to answer, and none is waiting on anybody else.

Three are settled. The message inspector lives in a Settings tab behind the protected
settings lock, which is what is built. Section 10 records the other two. This application is not a receiver, so inbound
is out of scope. The message type is ORU^R01 outbound, and only that.

1. **The character set policy.** Refuse, transliterate, or decide per destination? The
   recommendation is to refuse by default, for the reason in section 2.2.
2. **Mandatory fields with no source**, such as a report author. Configured constant, a new
   field on the form, or refuse to send? The engine can do all three.
3. **Shared configuration.** A department with ten workstations will not want ten copies of
   a profile maintained by hand. Export and import already exists and would work; a single
   shared source is a larger piece of work. Which is wanted?
4. **Coupling.** Should an HL7 failure prevent the DICOM transmission, or are the two
   independent? The proposal defaults to independent, with coupling available per
   destination, because that preserves today's behaviour exactly.

---

## 12. Recommendation

Phases 2 and 3 are proceeding and depend on nothing outside this repository.

Answer question 1, the character set policy, before anything is transmitted anywhere. It is
the one question here with a patient safety answer rather than an engineering one, and the
code already written defaults to refusing rather than to quietly changing a name, so a
decision to do otherwise is a decision to change that default rather than to fill a gap.
