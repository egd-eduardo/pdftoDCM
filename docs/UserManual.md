# PDF to DICOM — User Manual

**Version 3.0 · For hospital staff**

This application takes a PDF document produced by another system, turns it into a DICOM
Encapsulated PDF object, and sends it to your PACS so that the document appears in the
patient's imaging record.

You do not need to understand DICOM to use it. If you can fill in a form, you can use
this application.

---

## Contents

1. [Before you start](#1-before-you-start)
2. [The main window at a glance](#2-the-main-window-at-a-glance)
3. [Step 1 — Choose the document or image](#3-step-1--choose-the-document-or-image)
   - [Patient details read from the file name](#patient-details-read-from-the-file-name)
4. [Step 2 — Enter the patient information](#4-step-2--enter-the-patient-information)
   - [Taking the patient from the worklist](#taking-the-patient-from-the-worklist)
5. [Step 3 — Check the destination](#5-step-3--check-the-destination)
6. [Sending the document](#6-sending-the-document)
7. [What to do when something goes wrong](#7-what-to-do-when-something-goes-wrong)
8. [Using a barcode scanner](#8-using-a-barcode-scanner)
9. [Templates](#9-templates)
10. [Settings](#10-settings)
11. [Getting help from your IT department](#11-getting-help-from-your-it-department)
12. [Frequently asked questions](#12-frequently-asked-questions)

---

## 1. Before you start

**You need three things:**

| | |
|---|---|
| **The PDF** | Already saved somewhere you can find it. |
| **The patient's details** | Patient ID, name, date of birth. |
| **The accession number** | From the request or the worklist. |

**You should also know**, from your IT department, that the workstation has been
registered with the PACS. Until that is done, transmission will fail no matter what you
type. See [section 11](#11-getting-help-from-your-it-department).

> **The application works offline.** The only time it uses the network is the moment it
> sends a document to the PACS. It never sends anything anywhere else.

---

## 2. The main window at a glance

Everything happens in one window, and it reads from top to bottom.

```
┌──────────────────────────────────────────────────────────────────────┐
│  PDF to DICOM                              Logs   Settings   About   │  ← header
│  Hospital de Exemplo                                                 │
├───────────────────────────────────────┬──────────────────────────────┤
│  1. Select the PDF document           │  3. Destination PACS         │
│  ┌─────────────────────────────────┐  │  ┌────────────────────────┐  │
│  │  Drag a PDF here, or Browse     │  │  │ Main PACS              │  │
│  └─────────────────────────────────┘  │  │ PACS@192.168.10.20:104 │  │
│                                       │  └────────────────────────┘  │
│  2. Patient information               │   [Test connection]          │
│  [Template ▾] [From worklist...]      │                              │
│  Patient ID *      Accession no. *    │                              │
│  [_____________]   [_____________]    │  Results appear here after   │
│  Surname *         Given name         │  you send.                   │
│  [_____________]   [_____________]    │                              │
│  Date of birth *   Sex                │                              │
│  [__/__/____]      [Not specified ▾]  │                              │
├───────────────────────────────────────┴──────────────────────────────┤
│  [View DICOM tags] [Export logs]     [Generate only] [Generate and   │  ← actions
│                                                        send        ] │
├──────────────────────────────────────────────────────────────────────┤
│  ● Ready                                                             │  ← status bar
└──────────────────────────────────────────────────────────────────────┘
```

Fields marked with a red asterisk (**\***) are mandatory.

**The coloured dot in the status bar tells you where you are:**

| Dot | Meaning |
|:---:|---|
| ⚪ Grey | Ready. Waiting for you. |
| 🔵 Blue | Working. Generating or sending. |
| 🟢 Green | Completed successfully. |
| 🔴 Red | Something failed. Read the message. |

---

## 3. Step 1 — Choose the document or image

**From version 2.0 you can convert images as well as PDF documents.** JPEG, PNG, TIFF,
BMP, GIF, WebP and TGA are all accepted, which covers anything a scanner, a camera or a
phone is likely to produce.

You can select a file in three ways. Use whichever suits you.

**a) Drag and drop.** Drag the file from Windows Explorer or from your desktop and drop it
anywhere on the window. The drop area highlights when the file will be accepted.

**b) Browse.** Click **Browse...** and pick the file.

**c) Automatically.** If your IT department has switched on folder monitoring, a PDF saved
into the watched folder is picked up on its own. The status bar says *Picked up from the
monitored folder*.

Once a file is selected you will see its name, size, date and page count, and a line
describing what it was recognised as. **Check these before continuing.** The page count in
particular is a quick way to confirm you have the whole report and not a single page.

### Patient details read from the file name

If your IT department has switched this on as well, the patient fields fill in by
themselves from the name of the file that was picked up. A document produced by another
system usually carries the identifiers in its name already, so there is nothing to retype.

A file called

```
9999999999_JohnDoe_TEST123451.pdf
```

fills in Patient ID *9999999999*, name *JohnDoe* and Accession Number *TEST123451*. The
status bar says *The patient fields were filled from the file name. Check them before
sending.*

Three things are worth knowing.

- **Check what was filled in.** Anybody can rename a file, so a file name is not proof of
  identity. Nothing is sent until you press **Convert and Send** and confirm, exactly as
  before. The whole point of the confirmation is this moment.
- **Empty fields are normal.** Most naming schemes carry no date of birth, so the date of
  birth is left empty and the sex is recorded as *Other* rather than guessed at. Where the
  date of birth is genuinely unknown, an empty one is correct; an invented one is not.
- **A name that is not recognised changes nothing.** If the file name does not follow the
  agreed layout, the form is simply left for you to fill in by hand, which is how the
  application behaved before this feature existed.

**The study date comes from the file.** The date and time the file was created are recorded
as the study date and time, because that is normally when the other system issued the
document. The date the DICOM object itself was created is recorded separately, so both are
preserved.

**The file is put away afterwards.** Once a document has been dealt with, the original is
moved into an **Imported** folder beside the watched folder so that it is not picked up
again. If something went wrong, it goes into a **Failed** folder instead, where it can be
looked at. Nothing is ever deleted and nothing is ever overwritten; if a file of the same
name is already there, a number is added to the new one.

If the fields fill in wrongly, or do not fill in at all, your IT department can change the
naming layout, which is a setting and can be matched to whatever the other system produces.
Section 11 says what to tell them.

### What happens to an image

A line under the file details tells you exactly what will be produced, for example
*JPEG, 2480 x 3508, greyscale. It will become one DICOM image.*

- **A single image** becomes one DICOM image.
- **A scan with several pages**, which usually means a multi page TIFF, becomes one DICOM
  image per page. They all stay together as one item in the archive, and a viewer lets you
  page through them.

Two things are worth knowing:

- **A greyscale scan stays greyscale.** The application looks at the actual pixels rather
  than trusting the file, so a scanner that saves a black and white page as a colour file
  still produces a greyscale DICOM image. That is about a third of the size, and nothing is
  lost.
- **The DICOM image is larger than the file you started with**, often much larger, because
  DICOM stores the picture uncompressed. A four megabyte photograph can become a thirty
  megabyte DICOM image. This is normal and is what archives expect. If a transfer seems
  slow, this is why.

**It is not one file at a time only for PDFs.** The same rule applies to images: drop one
file, convert it, then drop the next.

> **Only one file at a time.** If you drop several files nothing happens. This is
> deliberate: guessing which one you meant is exactly how a document ends up filed
> against the wrong patient.

### If the document is refused

| Message | What it means | What to do |
|---|---|---|
| *does not exist* | The file was moved or deleted after you chose it. | Find it again. |
| *is empty* | The file has no content. | The system that produced it did not finish. Produce it again. |
| *does not begin with a PDF header* | It is not really a PDF, whatever the name says. | Ask the sending system for a real PDF. |
| *is neither a PDF nor an image in a supported format* | The file is something else entirely, such as a Word document or a spreadsheet. | Print or export it to PDF first, then convert that. |
| *is damaged and could not be read* | The image file is incomplete or corrupt. | Scan or export it again. |
| *is password protected* | The PDF is encrypted. | Save an unprotected copy and select that instead. Nobody could open an encrypted document from the PACS. |
| *may be damaged or still being written* | The file is incomplete. | Wait a few seconds and try again. |

---

## 4. Step 2 — Enter the patient information

**This is the most important step in the whole process.** Everything else is mechanical;
this is the part where a mistake reaches the patient's record.

### Taking the patient from the worklist

**If your department has a modality worklist, use it.** Typing a patient number by hand
is the single likeliest way a document ends up in the wrong patient's record, and the
worklist removes that step completely.

Press **From worklist...** above the patient fields. The button only appears when your IT
department has configured a worklist; if you cannot see it, there is nothing to connect
to and you should fill the form in as usual.

A window opens showing the procedures scheduled for today:

```
┌────────────────────────────────────────────────────────────────────────────┐
│  Modality Worklist                                                         │
│  Choose the scheduled procedure this document belongs to.                  │
│                                                                            │
│  Patient ID contains      Surname contains                                 │
│  [________________]       [________________]   [Search]   [Show all]       │
│                                                                            │
│  Patient ID │ Name                  │ Date of birth │ Accession │ Procedure│
│  P0000002   │ GONÇALVES, JOÃO MIGUEL│ 1970-09-03    │ ACC...002 │ Relat... │
│  P0000006   │ MARTINS, BEATRIZ      │ 2001-04-18    │ ACC...006 │ Radio... │
│                                                                            │
│  Will be used                                                              │
│  GONÇALVES, JOÃO MIGUEL                                                    │
│                                       [Cancel]    [Use this patient]       │
└────────────────────────────────────────────────────────────────────────────┘
```

- **Leave both boxes empty** to see the whole list. Type part of a surname or a patient
  number and press **Search** to narrow it.
- Double click a row, or select it and press **Use this patient**.
- The patient fields are then filled in for you, and the accession number comes from the
  order rather than from the paperwork on the desk.

**Check the "Will be used" line before pressing the button.** It shows exactly what will
go into the patient's record. Usually it matches the row above it. Occasionally it is
shorter, which is explained below.

**Why a very long name is sometimes shortened.** DICOM allows 64 characters for a name.
A small number of Portuguese names are longer than that, and the standard gives no way to
send them whole. The surname is kept complete, because that is what an archive files and
searches by, and the given name is shortened at a word boundary so it still reads as a
name. *ALBUQUERQUE MONTENEGRO DOS SANTOS RODRIGUES, JOSÉ ANTÓNIO FRANCISCO XAVIER* is
sent as *ALBUQUERQUE MONTENEGRO DOS SANTOS RODRIGUES, JOSÉ ANTÓNIO*. The full name stays
in the worklist, the shortening is recorded in the log, and nothing interrupts you.

**Middle names are not carried across.** The department does not use them, and leaving
them out means they cannot eat into the characters the first and last name need.

**The worklist is read only.** Nothing is ever sent back to it and no patient data leaves
your workstation when you open it. If you pick the wrong row, simply open the window
again and pick another: the whole form is replaced, never mixed.

### If nothing comes back

| What you see | What it usually means |
|---|---|
| *The worklist returned no scheduled procedures for this query* | The patient is not scheduled today. Ask your IT department to widen the date range, or fill the form in by hand. |
| *could not be reached* | The worklist server is down, or the address is wrong. Fill the form in by hand and tell your IT department. |
| *rejected the connection* | This workstation is not registered on the worklist server. Your IT department has to do that once. |
| *More results were available than can be shown* | Narrow the search with a surname or a patient number. |

You can always type the details yourself. The worklist is a convenience, never a
requirement.

### Mandatory fields

Fill these in yourself when you are not using the worklist.

| Field | Notes |
|---|---|
| **Patient ID** | The hospital number. It must match the PACS exactly, character for character. |
| **Surname** | Family name. |
| **Date of birth** | Pick from the calendar or type it. |
| **Accession number** | From the request. This is what lets the PACS connect the document to the order. Maximum 16 characters. |

### Optional fields

**Given name**, **Sex**, **Referring physician**, **Document title**, **Study
description**, **Series description**. These make the document easier to find later, but
nothing depends on them.

### Accented and international names

Type the name exactly as it appears in the patient record, accents included. *João*,
*Gonçalves*, *Aparício* are all handled correctly and reach the PACS unchanged.

If your IT department has set the character set to the older Latin 1 option, a name using
a non Latin alphabet is refused with an explanation rather than being silently mangled.

### The application will not let you continue if

- any mandatory field is empty,
- the date of birth is in the future or absurdly far in the past,
- the accession number is longer than 16 characters,
- a name contains a `^` or `\` character, which DICOM reserves for its own use.

The reason appears in the panel on the right. Correct it and carry on.

---

## 5. Step 3 — Check the destination

The right hand panel lists every PACS the document will be sent to. Most sites have
exactly one.

**Test connection** sends a harmless "are you there?" message (a DICOM C-ECHO) to each
destination and reports whether it answered. It sends **no patient data**. Use it freely
whenever you are unsure.

If the panel says *No destination is configured*, the application has not been set up yet.
Contact your IT department.

---

## 6. Sending the document

Click **Generate and send**.

### The confirmation dialog

Unless it has been switched off, a summary appears showing the patient ID, name, date of
birth, accession number, the file, and where it is going.

> **Read it.** This is the last moment at which a mistake can be caught for free. Once the
> document is in the PACS, removing it requires a PACS administrator.

Click **OK** to proceed, or **Cancel** to go back and correct something.

### What happens next

The status bar walks through the stages:

1. *Checking the information entered*
2. *Generating the DICOM object*
3. *Verifying … with C-ECHO*
4. *Sending to …*
5. *Completed*

For a normal report this takes a second or two.

### When it succeeds

The dot turns green and the results panel shows **OK** next to each destination. The
document is now in the patient's imaging record. Look for it in the PACS under the patient
ID and accession number you entered.

### Generate only

**Generate only** creates the DICOM file and saves it to disk **without sending it**. It
is useful when the network is down, or when your IT department has asked you for a sample
file. The file location is shown in the results panel.

---

## 7. What to do when something goes wrong

Nothing is lost when a transmission fails. **Your entries stay in the form** and the
generated file is kept on disk, so clicking **Retry** sends it again without retyping
anything.

| Message | Likely cause | What to do |
|---|---|---|
| *The connection was refused* | The PACS is not listening, or the port is wrong. | Click **Test connection**. If it also fails, contact IT. |
| *The host name could not be resolved* | The PACS address is wrong or DNS is down. | Contact IT. |
| *timed out* | A firewall is blocking the connection, or the network is down. | Check other network applications work, then contact IT. |
| *rejected the association* | The PACS does not recognise this workstation. | The workstation's AE Title needs registering on the PACS. Contact IT. |
| *aborted the association* | The PACS may not accept PDF documents, or it is busy. | Try again shortly. If it persists, contact IT. |
| *rejected the document* | The PACS received it but refused to store it. | Note the status code shown and send it to IT. |
| *did not pass validation* | An internal problem produced a faulty object. | Nothing was sent. Export the logs and contact IT. |

Where several destinations are configured, each is reported separately. One destination
failing does not stop the others.

---

## 8. Using a barcode scanner

Most hospital scanners behave as a keyboard, and those work with no configuration.

1. Click into **Patient ID** (it already has the cursor when the application opens).
2. Scan the wristband or the request.
3. The value appears. If your scanner sends an Enter afterwards, the cursor moves on.

Scan into **Accession number** the same way.

A trailing Enter or carriage return from the scanner is handled automatically. If a scan
produces a value the application refuses, with a message mentioning the barcode, the scan
was misread: clear the field and scan again.

---

## 9. Templates

If you convert the same kind of document repeatedly, save the repeating parts once.

**To save:** fill in the referring physician, study description, series description and
document title, then click **Save as template...** and give it a name.

**To use:** pick it from the list at the top of the patient section and click
**Templates**.

> **Templates never store patient identity.** No patient ID, name, date of birth or
> accession number is ever kept in a template. Reusing those values is the single most
> likely way to file a document against the wrong person, so the application does not
> make it possible.

---

## 10. Settings

Click **Settings** in the header. Most of these are set once by your IT department.

| Tab | Contains |
|---|---|
| **Institution** | Hospital and department name, and this workstation's AE Title. |
| **Destinations** | The PACS systems to send to, with a per destination **Test connection**. |
| **DICOM** | Character set, identifier generation, modality, default descriptions. Leave these alone unless your PACS administrator asks. |
| **Worklist** | The modality worklist server. Usually locked with a password held by whoever installed the application; pointing it at the wrong server would put the wrong patients in front of you. |
| **Folders** | Where files and logs are kept, and folder monitoring. |
| **Appearance** | Language, dark mode, and whether to confirm before sending. |

Two settings are worth knowing about as a user:

- **Dark mode** — easier on the eyes in a darkened reporting room.
- **Language** — English and Portuguese (Portugal). Changes immediately.

> **Leave "Ask for confirmation before sending" switched on.** It is the last check that
> the document matches the patient.

---

### Putting several files in one study

Normally each file you convert becomes its own study in the archive. If your department
prefers everything for one patient to appear together, your IT department can switch on
**study grouping** in Settings.

With grouping on, every file you convert for the **same Patient ID** joins one study. Each
file still appears separately inside it, so nothing is merged or lost; they are simply
filed together.

Two things to know:

- **Changing the Patient ID starts a new study.** So does closing the application. This is
  deliberate: a patient's next visit is a new examination and must not be added to an old
  one.
- **The status area shows the study being used** while grouping is active, so you can see
  it is working.

---

## 11. Getting help from your IT department

### Export the logs first

Click **Export logs**, save the zip file, and attach it to your request. It contains the
diagnostic log, the audit trail and the configuration. **It does not contain the PDF or
any image data.**

### What your IT department needs to have done

For transmission to work at all, the PACS administrator must have registered:

1. this workstation's **AE Title** (shown in Settings → Institution),
2. this workstation's **IP address**,
3. permission to send the **Encapsulated PDF Storage** SOP class.

Point 3 is the one most often missed. A PACS can be perfectly reachable and still refuse
PDF documents because that SOP class was never enabled.

If you are using the worklist, the same registration has to exist on the **worklist
server** as well. It is a separate system from the PACS, and being registered on one does
not register you on the other.

### Useful details to quote

- The exact message from the results panel.
- The DICOM status code, if one is shown.
- The SOP Instance UID from a successful run, if you are asking why a document cannot be
  found in the PACS.

---

## 12. Frequently asked questions

**Does this send anything over the internet?**
No. The only network connection it ever makes is to the PACS addresses configured by your
IT department, on your own hospital network.

**Where are the files kept?**
Under your own Windows profile, in `%APPDATA%\PdfToDicom`. Use **Open output folder** in
the results panel to go there directly.

**Can I send the same document twice?**
Yes, and each attempt creates a distinct object, so the PACS will hold two copies. If you
sent it once successfully, do not send it again unless you mean to.

**I sent it to the wrong patient. What now?**
Contact your PACS administrator immediately. The application cannot recall a document once
the archive has accepted it. Note the SOP Instance UID from the results panel, because it
identifies the exact object that needs removing.

**The document is in the PACS but under the wrong study.**
That usually means the accession number did not match the order. Check what you entered
against the request, then ask your PACS administrator to reconcile it.

**Why is the file so large?**
The application is a single self contained program: the whole .NET runtime is inside it.
That is what allows it to be installed by copying one file, with no runtime, no Java and
no other component to install first.

**Do I need administrator rights?**
No. Neither to install it nor to run it.

**The "From worklist..." button is not there.**
No worklist has been configured on this workstation. Ask your IT department whether your
department has one. Until then, fill the patient fields in by hand as before.

**The name in the worklist is longer than the name that was sent.**
DICOM allows 64 characters for a name and a few names are longer. The surname is kept
whole and the given name shortened, so the record still identifies the right person. This
is expected and is recorded in the log.

**Can the worklist be changed back by the order being updated?**
No. The details are copied into the form at the moment you press *Use this patient*.
Nothing is re-read afterwards, and nothing is ever written back to the worklist.

---

*For technical detail, see `TechnicalDocumentation.md` and `DicomConformanceStatement.md`
in the same folder.*
