# PDF to DICOM — User Manual

**Version 1.1 · For hospital staff**

This application takes a PDF document produced by another system, turns it into a DICOM
Encapsulated PDF object, and sends it to your PACS so that the document appears in the
patient's imaging record.

You do not need to understand DICOM to use it. If you can fill in a form, you can use
this application.

---

## Contents

1. [Before you start](#1-before-you-start)
2. [The main window at a glance](#2-the-main-window-at-a-glance)
3. [Step 1 — Choose the PDF](#3-step-1--choose-the-pdf)
4. [Step 2 — Enter the patient information](#4-step-2--enter-the-patient-information)
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

## 3. Step 1 — Choose the PDF

You can do this in three ways. Use whichever suits you.

**a) Drag and drop.** Drag the PDF from Windows Explorer or from your desktop and drop it
anywhere on the window. The drop area highlights when the file will be accepted.

**b) Browse.** Click **Browse...** and pick the file.

**c) Automatically.** If your IT department has switched on folder monitoring, a PDF saved
into the watched folder is picked up on its own. The status bar says *Picked up from the
monitored folder*.

Once a document is selected you will see its name, size, creation date and page count.
**Check these before continuing.** The page count in particular is a quick way to confirm
you have the whole report and not a single page.

> **Only one file at a time.** If you drop several files nothing happens. This is
> deliberate: guessing which one you meant is exactly how a document ends up filed
> against the wrong patient.

### If the document is refused

| Message | What it means | What to do |
|---|---|---|
| *does not exist* | The file was moved or deleted after you chose it. | Find it again. |
| *is empty* | The file has no content. | The system that produced it did not finish. Produce it again. |
| *does not begin with a PDF header* | It is not really a PDF, whatever the name says. | Ask the sending system for a real PDF. |
| *is password protected* | The PDF is encrypted. | Save an unprotected copy and select that instead. Nobody could open an encrypted document from the PACS. |
| *may be damaged or still being written* | The file is incomplete. | Wait a few seconds and try again. |

---

## 4. Step 2 — Enter the patient information

**This is the most important step in the whole process.** Everything else is mechanical;
this is the part where a mistake reaches the patient's record.

### Mandatory fields

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
| **Folders** | Where files and logs are kept, and folder monitoring. |
| **Appearance** | Language, dark mode, and whether to confirm before sending. |
| **Licence** | The licence code for this site. Your IT department enters this once. If the code is missing or has expired, the application still converts and saves documents but will not send them to the PACS. |

Two settings are worth knowing about as a user:

- **Dark mode** — easier on the eyes in a darkened reporting room.
- **Language** — English and Portuguese (Portugal). Changes immediately.

> **Leave "Ask for confirmation before sending" switched on.** It is the last check that
> the document matches the patient.

### If you see a licence message

If there is a problem with the licence for this site, a coloured band appears under the
title bar explaining what is wrong.

**You can still work.** You can still choose a document, enter the patient information,
and generate and save the DICOM file. Only sending to the PACS is affected.

To enter or correct the code:

1. Click **Settings**, then the **Licence** tab.
2. Paste the code your supplier sent you. It is one long line beginning `PDFDCM1.` and it
   does not matter if your email program has split it across several lines.
3. The **Status** underneath tells you at once whether the code is accepted.
4. Click **Save**.

If the message says the licence belongs to a different workstation, send your supplier the
identifier shown under **This workstation**, which looks like `A1B2-C3D4-E5F6-7890`. The
**Copy** button puts it on the clipboard so you can paste it into an email. It contains no
personal information.

If a licence has expired, the application keeps sending for a further fourteen days and
warns you each time, so that nobody is ever cut off without notice. Tell whoever manages
your contracts as soon as you see the warning.

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

---

*For technical detail, see `TechnicalDocumentation.md` and `DicomConformanceStatement.md`
in the same folder.*
