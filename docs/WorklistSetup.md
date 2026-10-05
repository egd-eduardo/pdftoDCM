# Modality Worklist — Setup and Commissioning

**For hospital IT and the PACS administrator.**
Day to day use is covered in the user manual; this document is about getting the worklist
working in the first place, and about what to check when it does not.

---

## Contents

1. [What this does, and what it does not](#1-what-this-does-and-what-it-does-not)
2. [What the worklist administrator must do](#2-what-the-worklist-administrator-must-do)
3. [Configuring the workstation](#3-configuring-the-workstation)
4. [The settings, one by one](#4-the-settings-one-by-one)
5. [Commissioning checklist](#5-commissioning-checklist)
6. [Troubleshooting](#6-troubleshooting)
7. [Long names](#7-long-names)
8. [Why the settings are locked](#8-why-the-settings-are-locked)

---

## 1. What this does, and what it does not

With a worklist configured, a **From worklist...** button appears above the patient
fields. The operator picks a scheduled procedure step and the Patient ID, name, date of
birth, sex, accession number and referring physician are filled in from the order rather
than typed.

**It does:**

- query a Modality Worklist SCP with C-FIND
  (`1.2.840.10008.5.1.4.31`, Modality Worklist Information Model — FIND);
- carry the order's **Study Instance UID** into the generated objects where the worklist
  supplies one, so the document is filed into the study the order created rather than
  into a study of its own;
- fall back cleanly: if the worklist is unreachable, the operator types the details in
  as before and nothing is blocked.

**It does not:**

- write anything back. There is no Modality Performed Procedure Step, so an order is
  never marked started or completed by this application. A site that closes orders
  through MPPS must continue to do so from the acquiring modality.
- store any part of the worklist. The response populates the form on screen and nothing
  else; nothing is cached on disk.
- listen for anything. As with every other DICOM activity in this product, the
  association is always outbound.

---

## 2. What the worklist administrator must do

The worklist server is a **different system from the PACS**. Being registered on the
archive does not register you on the worklist, and this is the single most common reason
a first attempt is rejected.

On the worklist server:

1. Register this workstation's **Calling AE Title**. By default that is the application's
   own AE Title, set in Settings → Institution; it can be overridden per worklist if the
   site needs a different identity there.
2. Register this workstation's **IP address**.
3. Permit **Modality Worklist Information Model — FIND** (`1.2.840.10008.5.1.4.31`) for
   that AE Title.

Confirm with the administrator that the server:

- accepts **Implicit VR Little Endian** (`1.2.840.10008.1.2`). This is the DICOM default
  transfer syntax that every SCP is required to support, and is the only one proposed for
  the query;
- returns **`ISO_IR 192`** (UTF-8) where names carry accents, or at least `ISO_IR 100`.
  A server configured to return no Specific Character Set at all will send accented
  characters that cannot be interpreted reliably;
- populates **`StudyInstanceUID`** in its responses. Ask explicitly. Everything else works
  without it, but without it the document cannot join the study the order created.

---

## 3. Configuring the workstation

Either in **Settings → Worklist**, or by editing `config.json` before deployment.
`samples/config.sample.json` carries the full annotated section. The shortest workable
form is:

```json
"Worklist": {
  "IsEnabled": true,
  "CalledAeTitle": "WORKLIST",
  "Host": "192.168.10.40",
  "Port": 104,
  "DateRange": "Today"
}
```

`IsEnabled` controls whether the button appears at all. Leave it false until the server
side registration is done: a button that always fails is worse than no button.

---

## 4. The settings, one by one

| Setting | Default | What it is for |
|---|---|---|
| `IsEnabled` | `false` | Whether the worklist is offered to the operator |
| `CalledAeTitle` | `WORKLIST` | The worklist server's own AE Title |
| `Host`, `Port` | — , `104` | Where it listens |
| `CallingAeTitleOverride` | empty | Leave empty to use the workstation's own AE Title. Set it only where the worklist server insists on a different identity from the archive |
| `Modality` | empty | Match key. Empty matches every modality, which is usually what a department filing documents wants |
| `ScheduledStationAeTitle` | empty | Match key. Empty matches every station, which is usually right: the document is filed from a desk, not from the scheduled device |
| `DateRange` | `Today` | `Today`, `YesterdayAndToday`, `LastSevenDays`, `AnyDate` or `Explicit` |
| `ExplicitFrom`, `ExplicitTo` | `null` | Only used with `Explicit`. Either may be left null, in which case today is used for that end |
| `TimeoutSeconds` | `30` | Covers the association and the response |
| `MaximumResults` | `500` | A guard against a mistyped query returning an entire schedule. The window says when the list was cut short |
| `UseTls`, `AcceptAnyTlsCertificate` | `false` | As for a destination. Accepting any certificate disables authentication of the server and should not be used on a routable network |

**An empty match key is not a missing value.** In DICOM an empty key means "match
anything and tell me what it was", which is exactly what "any modality" should do, so
leaving these blank is a legitimate and common choice rather than an incomplete
configuration.

**`AnyDate` sends no date key at all.** A busy department's worklist can hold thousands of
steps, so it is offered but is not the default: an unbounded query is slow for the server
and unhelpful on screen.

---

## 5. Commissioning checklist

- [ ] The workstation's Calling AE Title and IP are registered on the **worklist server**,
      not only on the PACS.
- [ ] `1.2.840.10008.5.1.4.31` is permitted for that AE Title.
- [ ] `IsEnabled` is true and the host, port and Called AE Title are correct.
- [ ] **Test connection** on the Worklist tab succeeds. It sends a C-ECHO only and carries
      no patient data.
- [ ] Opening **From worklist...** returns today's scheduled steps.
- [ ] A returned name with accents displays correctly.
- [ ] The returned entries carry an **Accession Number**.
- [ ] The returned entries carry a **Study Instance UID** — confirm with the worklist
      administrator if they do not.
- [ ] Convert one non clinical document from a worklist entry with **Generate only**, then
      **View DICOM tags**, and confirm the Patient ID, Accession Number and Study Instance
      UID match the order.
- [ ] Send it, and confirm it appears in the archive **under the study the order created**,
      beside the images rather than in a study of its own.

---

## 6. Troubleshooting

| What the operator sees | What it means | What to do |
|---|---|---|
| No **From worklist...** button | `IsEnabled` is false, or no worklist is configured | Configure it in Settings → Worklist |
| *rejected the connection* | The worklist server does not recognise this AE Title | Register the Calling AE Title and IP on the worklist server |
| *could not be reached* | Nothing is listening, or a firewall is in the way | Check the host, the port and the route. Port 104 is privileged on some systems and sites often use 1070 or 2100 instead |
| *returned no scheduled procedures* | The query was answered and matched nothing | Widen `DateRange`, clear the `Modality` and `ScheduledStationAeTitle` match keys, and check the operator has not left a filter in the search boxes |
| Entries appear but have no Accession Number | The worklist server does not populate it | Ask the worklist administrator. Nothing on this side can supply it |
| Entries appear but documents still land in their own study | The worklist server does not populate `StudyInstanceUID` | Ask the worklist administrator. Until then, the configured study grouping applies |
| Accented names come back as `?` or mangled | The server is not returning `ISO_IR 192` | Ask the worklist administrator to enable it |
| *More results were available than can be shown* | More matches than `MaximumResults` | Narrow the search, or raise the limit |

A failed worklist query never blocks anything. The operator fills the form in by hand and
carries on, and the failure is in the log with the AE Title, host and port that were
tried.

### Where to look

**Export logs** in the main window produces a zip containing the diagnostic log and the
audit trail. Every worklist query is recorded with how many steps came back and from
where. **It contains no patient documents and no image data.**

---

## 7. Long names

DICOM allows **64 characters** for a Person Name component group, which is the whole
`Family^Given` pair and not 64 each. A small number of Portuguese names are longer than
that, and no implementation can send them whole: the limit is the standard's.

Where a worklist entry exceeds it:

- the **surname is kept complete**, because that is what an archive indexes and what a
  clinician searches by;
- the **given name is shortened at a word boundary**, so it still reads as a name;
- where the surname alone is over the limit, the surname is cut at a word boundary and
  the given name dropped.

This happens **without interrupting the operator**, as the department asked. It is not
hidden: the worklist window shows the name that will actually be used underneath the
selected entry, and every shortening is written to the audit log with the original
length. If a clinician later asks why a name in the archive is shorter than the one in
the RIS, the log answers it.

---

## 8. Why the settings are locked

The Worklist tab in Settings may be protected by a password. It is set when the software
is built and there is no interface to change it, which is deliberate.

Pointing a workstation at the wrong worklist server does not produce an error. It
produces a list of **the wrong patients**, presented exactly as convincingly as the right
ones, and whoever is at the desk has no way of telling. That is the failure the lock
exists to prevent.

Being honest about what it is: the check runs on the workstation itself, so it deters a
colleague who should not be in there. It is not a defence against someone determined to
get past it, and no password a desktop application checks by itself could be.

If the Worklist tab opens without asking for a password, no password was compiled into
that build. The tab says so rather than implying a protection that is not there.

---

*See also: the DICOM Conformance Statement, section 2.3.3, for the exact match keys,
return keys and status handling.*
