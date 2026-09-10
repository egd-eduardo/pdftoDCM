# DICOM Conformance Statement

**Product:** PDF to DICOM
**Version:** 1.1.0
**Document version:** 1.0
**Date:** 2026

---

## 0. Purpose and scope

This statement describes the DICOM capabilities of PDF to DICOM. It is intended for PACS
administrators and integration engineers assessing whether the product will interoperate
with a given archive.

It follows the structure of **DICOM PS3.2**. It is a conformance statement, not a
certification: interoperability should always be confirmed by testing against the specific
archive concerned.

---

## 1. Implementation model

### 1.1 Application data flow

```
        ┌──────────────────────────────────────────────────────┐
        │              PDF to DICOM workstation                │
        │                                                      │
   PDF ─┼──►  Encapsulate  ──►  Validate  ──►  Storage SCU ────┼──►  Remote
        │     (local)           (local)         Verification   │     Storage SCP
        │                                       SCU            │     (PACS)
        └──────────────────────────────────────────────────────┘
                                                    ▲
                                                    │
                              Association initiated by this product only.
                              No listening port. No inbound associations.
```

The product acts **exclusively as an SCU**. It never listens for, nor accepts, an
association.

### 1.2 Functional definition of the Application Entity

A single AE performs all DICOM activity. It is invoked when the operator sends a document,
tests a destination, or the command line tool is run.

For each configured and enabled destination, in sequence:

1. Optionally establish an association and issue **C-ECHO** (Verification). A failure here
   stops the exchange for that destination; no patient data is transmitted.
2. Establish an association and issue **C-STORE** for the generated instance.
3. Release the association.

Retries are applied per destination according to its configuration. A failure against one
destination does not prevent the others from being attempted.

### 1.3 Sequencing of real world activities

- Demographics are supplied by the operator, or by a barcode scan. **No Modality Worklist
  query is performed** (see section 8).
- Identifiers are generated locally at the moment of conversion.
- A new Study Instance UID is generated per document, unless the operator supplies an
  existing one to file the document into an existing study.

---

## 2. AE specifications

### 2.1 Application Entity: PDF to DICOM SCU

Provides standard conformance to the following SOP classes as an **SCU**:

| SOP Class Name | SOP Class UID | Role |
|---|---|---|
| Verification | 1.2.840.10008.1.1 | SCU |
| Encapsulated PDF Storage | 1.2.840.10008.5.1.4.1.1.104.1 | SCU |

No SOP class is supported as an SCP.

### 2.2 Association establishment policies

#### 2.2.1 General

| Parameter | Value |
|---|---|
| Maximum PDU size (receive) | 16,384 bytes (toolkit default) |
| Maximum PDU size (send) | Negotiated with the association acceptor |
| Implementation Class UID | `2.25.164837465109381728394657283910473625` |
| Implementation Version Name | `PDFTODICOM_1_1` |
| Application Context Name | `1.2.840.10008.3.1.1.1` |
| Asynchronous operations | Not supported |
| SOP class extended negotiation | Not supported |
| Maximum simultaneous associations | 1 |

#### 2.2.2 Number of associations

One at a time. Destinations are contacted sequentially. Verification and Storage use
separate associations.

#### 2.2.3 Configurable parameters

| Parameter | Default | Range |
|---|---|---|
| Calling AE Title | `PDF2DCM` | 1–16 characters, AE VR |
| Called AE Title | per destination | 1–16 characters, AE VR |
| Remote host | per destination | Host name or IP address |
| Remote port | 104 | 1–65535 |
| Association timeout | 30 s | 1–600 s |
| Retry count | 2 | 0–10 additional attempts |
| Retry delay | 3 s | 0–120 s |
| Verify before send | Enabled | Boolean |
| TLS | Disabled | Boolean, per destination |

A per destination Calling AE Title override is available for sites that require a
different identity per archive.

### 2.3 Association initiation policy

#### 2.3.1 Activity: Verify destination

**Description.** Confirms the peer is reachable and will accept an association, either as
a pre-flight check before a store or from the operator's *Test connection* action. **No
patient data is transmitted.**

**Proposed presentation contexts:**

| Abstract Syntax | Transfer Syntax | Role | Extended Negotiation |
|---|---|---|---|
| Verification (1.2.840.10008.1.1) | Implicit VR Little Endian (1.2.840.10008.1.2) | SCU | None |

**Response handling:**

| Status | Behaviour |
|---|---|
| Success (0000) | Proceed to store, or report success. |
| Any other status | Report verification failure. The store is abandoned. |
| No response, timeout, refusal | Report a transport failure with a diagnostic message. Retry if configured. |

#### 2.3.2 Activity: Store document

**Description.** Transmits one Encapsulated PDF Storage SOP Instance.

**Proposed presentation contexts:**

| Abstract Syntax | Transfer Syntax | Role | Extended Negotiation |
|---|---|---|---|
| Encapsulated PDF Storage (1.2.840.10008.5.1.4.1.1.104.1) | Explicit VR Little Endian (1.2.840.10008.1.2.1) | SCU | None |
| Encapsulated PDF Storage (1.2.840.10008.5.1.4.1.1.104.1) | Implicit VR Little Endian (1.2.840.10008.1.2) | SCU | None |

Both are offered so that negotiation succeeds against archives with a narrow context list.
Objects are encoded in Explicit VR Little Endian.

**Response handling:**

| Status | Meaning | Behaviour |
|---|---|---|
| `0000` | Success | Reported as accepted. |
| `B000`, `B006`, `B007` | Warning: coercion or discarded elements | **Reported as accepted**, with the warning shown to the operator and written to the log. The archive stored the object. |
| `A7xx` | Refused: out of resources | Treated as transient. Retried if configured. |
| `A9xx` | Error: data set does not match SOP class | Permanent. Not retried. |
| `Cxxx` | Cannot understand | Permanent. Not retried. |
| `0122` | SOP class not supported | Permanent. Not retried. Usually means the archive has not enabled Encapsulated PDF Storage. |
| Any other failure | | Reported verbatim with its code. |

**Association rejection and abort** are reported with the reason, mapped to operator
facing guidance.

### 2.4 Association acceptance policy

**Not applicable.** The product never accepts an association.

---

## 3. Communication profiles

### 3.1 Supported communication stacks

DICOM Upper Layer Protocol over TCP/IP, per PS3.8.

### 3.2 TCP/IP stack

Provided by the host operating system. IPv4 and IPv6 are both supported; the address family
follows host name resolution.

### 3.3 TLS

Optional and configurable per destination. When enabled, the association is established
over TLS using the platform's negotiated cipher suites.

A per destination option to accept any server certificate is available for closed networks
using self signed certificates. It disables authentication of the receiving system and the
settings dialog warns accordingly; it should not be used on a routable network.

---

## 4. Extensions, specialisations and privatisations

### 4.1 Private data elements

One private block is reserved, holding a single attribute.

| Private Creator | Group | Element | VR | Description |
|---|---|---|---|---|
| `PDFTODICOM` | `0x7771` | `0x01` (as reserved, e.g. `(7771,1001)`) | IS | Number of pages in the encapsulated PDF |

The block is reserved correctly per **PS3.5 section 7.8**. Conformant systems that do not
recognise the private creator ignore it.

**Rationale.** The standard defines no attribute for the page count of an encapsulated
document. Writing it to a reserved private block is preferable to forcing the value into an
unrelated standard attribute.

**This can be disabled** (`RecordPageCountInPrivateTag: false`) for archives intolerant of
private data elements.

### 4.2 Standard extended SOP classes

None.

---

## 5. Configuration

### 5.1 AE Title and port

| Item | Configurable | Notes |
|---|---|---|
| Calling AE Title | Yes | Must be registered on each receiving archive |
| Called AE Title | Yes, per destination | Must match the archive's own AE Title |
| Remote host and port | Yes, per destination | |
| Local listening port | Not applicable | The product does not listen |

### 5.2 Configuration parameters

Stored in `%APPDATA%\PdfToDicom\config.json`, per user, requiring no administrator rights.
A fully annotated example is provided in `samples/config.sample.json`.

---

## 6. Media storage

The product writes Part 10 compliant files with complete File Meta Information. It does
not implement a Media Storage Application Profile and performs no media creation.

| Item | Value |
|---|---|
| File Meta Information Version | `00\01` |
| Transfer Syntax UID | 1.2.840.10008.1.2.1 |
| Implementation Class UID | `2.25.164837465109381728394657283910473625` |
| Implementation Version Name | `PDFTODICOM_1_1` |
| Source Application Entity Title | The configured Calling AE Title |

---

## 7. Created SOP instances — attribute detail

### 7.1 Encapsulated PDF Storage IOD (PS3.3 A.45.1)

| Module | Reference | Usage |
|---|---|---|
| Patient | C.7.1.1 | Mandatory |
| General Study | C.7.2.1 | Mandatory |
| Encapsulated Document Series | C.24.1 | Mandatory |
| General Equipment | C.7.5.1 | Mandatory |
| SC Equipment | C.8.6.1 | Mandatory |
| Encapsulated Document | C.24.2 | Mandatory |
| SOP Common | C.12.1 | Mandatory |

### 7.2 Attributes written

**Type** is the attribute type from the standard.
**Source** is where the value comes from: *Operator*, *Configuration*, *Generated* or
*Fixed*.

#### Patient module

| Attribute | Tag | VR | Type | Source | Notes |
|---|---|---|---|---|---|
| Patient's Name | (0010,0010) | PN | 2 | Operator | `Family^Given^Middle^Prefix^Suffix`, trailing empty components omitted |
| Patient ID | (0010,0020) | LO | 2 | Operator | Mandatory in the application |
| Patient's Birth Date | (0010,0030) | DA | 2 | Operator | `YYYYMMDD`. Mandatory in the application |
| Patient's Sex | (0010,0040) | CS | 2 | Operator | `M`, `F`, `O`, or empty |

#### General Study module

| Attribute | Tag | VR | Type | Source | Notes |
|---|---|---|---|---|---|
| Study Instance UID | (0020,000D) | UI | 1 | Generated or Operator | New unless an existing UID is supplied |
| Study Date | (0008,0020) | DA | 2 | Generated | Date of conversion |
| Study Time | (0008,0030) | TM | 2 | Generated | Time of conversion |
| Referring Physician's Name | (0008,0090) | PN | 2 | Operator | Free text reordered to PN form |
| Study ID | (0020,0010) | SH | 2 | Operator | Defaults to the accession number |
| Accession Number | (0008,0050) | SH | 2 | Operator | Mandatory in the application |
| Study Description | (0008,1030) | LO | 3 | Operator or Configuration | |

#### Encapsulated Document Series module

| Attribute | Tag | VR | Type | Source | Notes |
|---|---|---|---|---|---|
| Modality | (0008,0060) | CS | 1 | Configuration | Default `OT`; `DOC` selectable. See note below |
| Series Instance UID | (0020,000E) | UI | 1 | Generated | New for every instance |
| Series Number | (0020,0011) | IS | 1 | Configuration | Default `1` |

> **Note on Modality.** The defined term for encapsulated document series in the current
> standard is `DOC`. `OT` is the default here because it is accepted by essentially every
> archive in the field and was the specified requirement. Sites whose archive expects `DOC`
> can select it in Settings. Confirm which value your archive expects during commissioning.

#### General Equipment module

| Attribute | Tag | VR | Type | Source | Notes |
|---|---|---|---|---|---|
| Manufacturer | (0008,0070) | LO | 2 | Configuration | Default `PDF to DICOM` |
| Institution Name | (0008,0080) | LO | 3 | Configuration | Written when configured |
| Station Name | (0008,1010) | SH | 3 | Configuration | Defaults to the machine name |
| Institutional Department Name | (0008,1040) | LO | 3 | Configuration | Written when configured |
| Manufacturer's Model Name | (0008,1090) | LO | 3 | Fixed | `PDF to DICOM Converter` |
| Software Versions | (0018,1020) | LO | 3 | Fixed | Product version |

#### SC Equipment module

| Attribute | Tag | VR | Type | Source | Notes |
|---|---|---|---|---|---|
| Conversion Type | (0008,0064) | CS | 1 | Configuration | Default `WSD` (Workstation) |

#### Encapsulated Document module

| Attribute | Tag | VR | Type | Source | Notes |
|---|---|---|---|---|---|
| Instance Number | (0020,0013) | IS | 1 | Configuration | Default `1` |
| Content Date | (0008,0023) | DA | 2 | Generated | Date of conversion |
| Content Time | (0008,0033) | TM | 2 | Generated | Time of conversion |
| Acquisition DateTime | (0008,002A) | DT | 2 | Generated | Moment of conversion |
| Burned In Annotation | (0028,0301) | CS | 1 | Configuration | Default `YES` |
| Document Title | (0042,0010) | ST | 2 | Operator, PDF metadata or Configuration | |
| Concept Name Code Sequence | (0040,A043) | SQ | 2 | Generated | DCM 121181, "Document Title" |
| Document Class Code Sequence | (0040,E008) | SQ | 3 | Generated | DCM 121181, "Document Title" |
| MIME Type of Encapsulated Document | (0042,0012) | LO | 1 | Fixed | `application/pdf` |
| Encapsulated Document | (0042,0011) | OB | 1 | Generated | The PDF, verbatim, padded to even length |
| Series Description | (0008,103E) | LO | 3 | Operator or Configuration | |

#### SOP Common module

| Attribute | Tag | VR | Type | Source | Notes |
|---|---|---|---|---|---|
| Specific Character Set | (0008,0005) | CS | 1C | Configuration | `ISO_IR 192` default, `ISO_IR 100` selectable |
| SOP Class UID | (0008,0016) | UI | 1 | Fixed | 1.2.840.10008.5.1.4.1.1.104.1 |
| SOP Instance UID | (0008,0018) | UI | 1 | Generated | New for every instance |
| Instance Creation Date | (0008,0012) | DA | 3 | Generated | |
| Instance Creation Time | (0008,0013) | TM | 3 | Generated | |
| Instance Creator UID | (0008,0014) | UI | 3 | Fixed | The Implementation Class UID |
| Timezone Offset From UTC | (0008,0201) | SH | 3 | Generated | For example `+0100` |

### 7.3 Character set support

| Setting | Defined Term | Coverage |
|---|---|---|
| UTF-8 (default) | `ISO_IR 192` | Any Unicode text, including accented Portuguese, Cyrillic, Greek, CJK |
| Latin 1 | `ISO_IR 100` | Western European only |

Specific Character Set is always written, so the receiving system never has to infer the
encoding. When Latin 1 is configured, a name containing characters outside that repertoire
is **rejected with an explanation** rather than being silently corrupted.

### 7.4 Identifier generation

| Strategy | Form | Notes |
|---|---|---|
| UUID derived (default) | `2.25.<128 bit UUID as decimal>` | Per PS3.5 Annex B.2. No registration required |
| Registered root | `<root>.<n>.<yyyyMMddHHmmssfff>.<random>` | Root limited to 31 characters so UIDs stay within 64 |

Every generated UID is verified conformant before use. Identifiers are never reused: 20,000
consecutive identifiers per strategy are asserted distinct in the automated test suite.

---

## 8. Support of extended character sets and worklists

- Extended character sets: as section 7.3.
- **Modality Worklist: not supported.** Demographics are entered or scanned by the
  operator. This is the most significant functional limitation of the product and is
  recorded here so that a site can weigh the transcription risk.

---

## 9. Interoperability

### 9.1 Verified interoperability

The product has been verified end to end against **DCMTK** (`storescp`), the widely used
reference implementation, by an automated script included in the repository
(`build/verify-against-dcmtk.sh`). The verification confirms:

- C-ECHO is accepted;
- C-STORE is accepted with status `0000`;
- `dcmdump` parses the received object without error;
- every mandatory attribute is present and carries the expected value;
- an accented Portuguese patient name survives the round trip intact;
- the extracted PDF is **byte identical** to the source document.

### 9.2 Expected interoperability

The product produces standard conformant objects using a widely deployed toolkit and an
uncompressed transfer syntax. It is expected to interoperate with any archive that supports
Encapsulated PDF Storage as an SCP, including GE, Philips, Siemens, Agfa, Fujifilm, Sectra,
Carestream, Orthanc, dcm4chee and Conquest.

> **This is an expectation, not a claim of certification.** Interoperability with a
> specific product and version should be confirmed by testing during commissioning.

### 9.3 Commissioning checklist

For the PACS administrator:

- [ ] Register the workstation's **Calling AE Title** on the archive.
- [ ] Register the workstation's **IP address**.
- [ ] Enable **Encapsulated PDF Storage** (`1.2.840.10008.5.1.4.1.1.104.1`) as an accepted
      SOP class. *This is the step most often missed.*
- [ ] Confirm the archive accepts **Explicit VR Little Endian**.
- [ ] Confirm whether the archive expects `Modality` = `OT` or `DOC`, and set it
      accordingly.
- [ ] Confirm the archive accepts **ISO_IR 192**; if not, switch to `ISO_IR 100`.
- [ ] Confirm the archive tolerates **private data elements**; if not, disable the page
      count attribute.
- [ ] Test with *Test connection*, then with a non clinical document.
- [ ] Confirm the document appears under the correct Patient ID and Accession Number.
- [ ] Confirm a clinician can open the PDF from the archive's viewer.

---

## 10. Revision history

| Version | Date | Change |
|---|---|---|
| 1.0 | 2026 | Initial release |
