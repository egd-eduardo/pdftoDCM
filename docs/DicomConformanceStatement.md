# DICOM Conformance Statement

**Product:** PDF to DICOM
**Version:** 2.1.0
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
| Secondary Capture Image Storage | 1.2.840.10008.5.1.4.1.1.7 | SCU |

No SOP class is supported as an SCP.

### 2.2 Association establishment policies

#### 2.2.1 General

| Parameter | Value |
|---|---|
| Maximum PDU size (receive) | 16,384 bytes (toolkit default) |
| Maximum PDU size (send) | Negotiated with the association acceptor |
| Implementation Class UID | `2.25.164837465109381728394657283910473625` |
| Implementation Version Name | `PDFTODICOM_2_1` |
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
| Secondary Capture Image Storage (1.2.840.10008.5.1.4.1.1.7) | Explicit VR Little Endian (1.2.840.10008.1.2.1) | SCU | None |
| Secondary Capture Image Storage (1.2.840.10008.5.1.4.1.1.7) | Implicit VR Little Endian (1.2.840.10008.1.2) | SCU | None |

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
| Implementation Version Name | `PDFTODICOM_2_1` |
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
| Conversion Type | (0008,0064) | CS | 1 | Configuration | Default `WSD` (Workstation). Any defined term of PS3.3 C.8.6.1.1 may be selected: `WSD`, `SD`, `SI`, `DV`, `DI`, `DF`, `DRW`, `SYN` |

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

### 7.3 Secondary Capture Image Storage IOD (PS3.3 A.8.1)

Produced from an ordinary image file: JPEG, PNG, TIFF, BMP, GIF, WebP or TGA. The modules
written are Patient, General Study, General Series, General Equipment, SC Equipment,
General Image, Image Pixel and SOP Common.

**Pixel encoding.** Always uncompressed, 8 bits allocated, 8 bits stored, high bit 7,
unsigned.

| Source | Samples per Pixel | Photometric Interpretation | Planar Configuration |
|---|---|---|---|
| Carries colour | 3 | `RGB` | 0 (interleaved) |
| Greyscale, or colour channels all equal | 1 | `MONOCHROME2` | not written |

Whether a source carries colour is decided from its pixels rather than from its file
format. A flatbed scanner emitting an RGB JPEG of a black and white page therefore
produces a `MONOCHROME2` object: two thirds smaller, and a more honest description of the
content. Nothing is lost, because if red, green and blue are equal in every pixel there is
no colour present to discard.

**Object size.** Uncompressed pixel data is large. A twelve megapixel colour photograph
becomes an object of roughly 36 MB. Uncompressed Explicit VR Little Endian is used
regardless, because every conformant archive accepts it and it needs no codec library.
Sites whose archive limits object size should reduce the resolution before converting; the
application warns when a single page would exceed roughly 1.2 GB.

**Multi page sources.** A multi page TIFF, or an animated GIF, produces **one SOP Instance
per page**, all sharing one Series Instance UID and numbered sequentially in Instance
Number (0020,0013). Single frame Secondary Capture is used in preference to the multi
frame SOP Classes because it is the most widely supported image storage class in the
field, including on older archives.

| Attribute | Tag | VR | Type | Value |
|---|---|---|---|---|
| SOP Class UID | (0008,0016) | UI | 1 | 1.2.840.10008.5.1.4.1.1.7 |
| Modality | (0008,0060) | CS | 1 | Configurable, `OT` by default, `XC` available |
| Conversion Type | (0008,0064) | CS | 1 | Configurable; see 7.4 |
| Image Type | (0008,0008) | CS | 1 | `DERIVED\SECONDARY` |
| Instance Number | (0020,0013) | IS | 2 | Page number, from 1 |
| Patient Orientation | (0020,0020) | CS | 2C | Empty; it has no meaning for a scan |
| Burned In Annotation | (0028,0301) | CS | 1 | `YES` |
| Lossy Image Compression | (0028,2110) | CS | 3 | `01` for a JPEG or WebP source, `00` otherwise |
| Lossy Image Compression Method | (0028,2114) | CS | 3 | `ISO_10918_1` for a JPEG source |

**Modality is configured separately for images.** A site that sets the document modality
to `DOC` must not have its photographs labelled as encapsulated documents, so the two
settings are independent.

**Lossy history is recorded, not hidden.** PS3.3 C.7.6.1 requires Lossy Image Compression
to say so whenever the pixels have *ever* been lossily compressed, not merely when the
object itself is compressed. A JPEG scan has already lost information that no later step
recovers, and a clinician judging fine detail is entitled to learn that from the object.

**Burned In Annotation is always `YES`.** A scanned report or a clinical photograph almost
certainly carries the patient's name in its pixels. Asserting `YES` warns a receiving
system that the pixel data cannot be treated as anonymous, which is the safe answer when
the truth cannot be known.

---

### 7.4 Study and series construction

#### A converted PDF produces two series

Unless page rendering is switched off at the site, converting one PDF produces **two series
inside a single study**:

| Series | SOP Class | Contents |
|---|---|---|
| 1 | Encapsulated PDF Storage `1.2.840.10008.5.1.4.1.1.104.1` | The document, byte for byte |
| 2 | Secondary Capture Image Storage `1.2.840.10008.5.1.4.1.1.7` | One instance per page, Instance Number 1..n |

**Why both.** Many viewers will only open a study that contains an image, and an
Encapsulated PDF is not an image SOP Class. At such a site the viewer refuses to launch and
the report cannot be read at all, however conformant the object is. The rendered pages make
the report readable on screen; the encapsulated document remains authoritative and carries
selectable text.

Page images are rendered at 150 DPI by default, carry `ImageType` `DERIVED\SECONDARY`, and
record their origin in Derivation Description (0008,2111). A page whose colour channels are
equal everywhere is written as `MONOCHROME2`, which is the usual case for a report and a
third of the size.

Setting `RenderPdfPagesAsImages` to false restores the single object behaviour of every
version before 2.1.0.

#### How studies are formed

Each converted file always receives **its own Series Instance UID**. What varies is how
studies are formed, which the site chooses:

| Study grouping | Behaviour |
|---|---|
| Each file in its own study (default) | Every conversion creates a new Study Instance UID |
| Files for the same patient share one study | Conversions for the same Patient ID reuse one Study Instance UID, each still in its own series |

Grouping is held in memory for the duration of a session and is **never written to disk**.
A patient returning months later is having a new examination, and their new report must
not silently join the study created for the old one. Closing the application, or moving to
a different patient, starts a new study.

Patient identifiers are compared exactly, including case. They are opaque strings assigned
by another system, and treating `ab1` and `AB1` as the same patient would be a guess.

A Study Instance UID supplied by the operator overrides grouping entirely, which is how a
document is filed into a study that already exists in the archive.

---

### 7.5 Character set support

| Setting | Defined Term | Coverage |
|---|---|---|
| UTF-8 (default) | `ISO_IR 192` | Any Unicode text, including accented Portuguese, Cyrillic, Greek, CJK |
| Latin 1 | `ISO_IR 100` | Western European only |

Specific Character Set is always written, so the receiving system never has to infer the
encoding. When Latin 1 is configured, a name containing characters outside that repertoire
is **rejected with an explanation** rather than being silently corrupted.

### 7.6 Identifier generation

| Strategy | Form | Notes |
|---|---|---|
| UUID derived (default) | `2.25.<128 bit UUID as decimal>` | Per PS3.5 Annex B.2. No registration required |
| Registered root | `1.2.826.0.1.3680043.9.6008.<n>.<yyyyMMddHHmmssfff>.<random>` | 62 characters. `<n>` is 1 for Study, 2 for Series, 3 for SOP Instance |

The registered root `1.2.826.0.1.3680043.9.6008` is issued by Medical Connections Ltd to
the author of this software. It is fixed in the software and cannot be configured by a
site, since a registered root identifies its registrant.

**Uniqueness policy.** The registrant undertakes that UIDs beneath the prefix cannot be
duplicated. The suffix combines UTC time to the millisecond with fifteen digits of
cryptographically generated randomness, so identifiers created in the same millisecond on
different workstations remain distinct. No range beneath the prefix is delegated to any
site, and no site can be configured to issue under it.

Every generated UID is verified conformant before use. Identifiers are never reused: 20,000
consecutive identifiers per strategy are asserted distinct in the automated test suite.

**Which to choose.** The UUID derived form is the default and is unique by construction.
Select the registered root if your archive prefers, or requires, identifiers under a
registered arc. Both are conformant; neither needs anything configured at the site.

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
