// dcmjs-pdf-gen
// Generates DICOM Encapsulated PDF datasets for testing purposes.
//
// Required packages:
//   npm install dcmjs faker@5.5.3 moment inquirer@8 glob@8 dicom-dimse-native node-notifier
//
// Usage:
//   node dcmjs-pdf-gen.js
//   PDF_FILE=test.pdf node dcmjs-pdf-gen.js   (skip picker, use env var)

'use strict';

// ── Safe require ────────────────────────────────────────────────────────────
// Catches missing or broken modules at load time and shows a native OS
// notification BEFORE crashing, so errors are visible even if the terminal
// is in the background.
function safeRequire(moduleName) {
  try {
    return require(moduleName);
  } catch (err) {
    // Try to notify via node-notifier if already loaded; else use AppleScript
    // / PowerShell / zenity as last-resort native popups.
    const msg = `Missing or broken module: "${moduleName}"\n${err.message}`;
    try { require('node-notifier').notify({ title: 'DICOM PDF Gen — Load Error', message: msg, sound: true }); } catch {}
    nativeAlert('DICOM PDF Gen — Load Error', msg);
    console.error(`\n❌  ${msg}\n`);
    process.exit(1);
  }
}

/**
 * Last-resort native popup that works even before any npm package is loaded.
 * Uses PowerShell on Windows, osascript on macOS, zenity/xmessage on Linux.
 */
function nativeAlert(title, message) {
  const { execSync } = require('child_process');
  const safe = (s) => s.replace(/"/g, "'");   // avoid shell injection
  try {
    if (process.platform === 'win32') {
      execSync(
        `powershell -NoProfile -Command "` +
        `[System.Windows.Forms.MessageBox]::Show('${safe(message)}','${safe(title)}',[System.Windows.Forms.MessageBoxButtons]::OK,[System.Windows.Forms.MessageBoxIcon]::Error)"`,
        { stdio: 'ignore' }
      );
    } else if (process.platform === 'darwin') {
      execSync(`osascript -e 'display alert "${safe(title)}" message "${safe(message)}"'`, { stdio: 'ignore' });
    } else {
      // Linux: try zenity, then xmessage, then just print
      try {
        execSync(`zenity --error --title="${safe(title)}" --text="${safe(message)}"`, { stdio: 'ignore' });
      } catch {
        execSync(`xmessage -center "${safe(title)}: ${safe(message)}"`, { stdio: 'ignore' });
      }
    }
  } catch {
    // Non-fatal — message already printed to console above
  }
}

const fs        = safeRequire('fs');
const path      = safeRequire('path');
const os        = safeRequire('os');
const glob      = safeRequire('glob');          // glob@8 — CommonJS default export
const inquirer  = safeRequire('inquirer');      // must be inquirer@8
const faker     = safeRequire('faker');         // must be faker@5.5.3
const moment    = safeRequire('moment');
const dcmjs     = safeRequire('dcmjs');         // requires uuid@8 — run: npm install uuid@8
const dimse     = safeRequire('dicom-dimse-native');
const notifier  = safeRequire('node-notifier');

const { DicomMetaDictionary, DicomDict } = dcmjs.data;

//////////////////////////////////////////////////////////////////////////
// CONSTANTS

const EncapsulatedPdfSopClassUid            = '1.2.840.10008.5.1.4.1.1.104.1';
const ExplicitVrLittleEndianTransferSyntaxUid = '1.2.840.10008.1.2.1';
const ImplementationUid                     = '1.3.6.1.4.1.30071.8';

const PDF_SUBFOLDER_NAME      = 'dcm_pdfs';       // dedicated drop folder
const PDF_CONVERTED_FOLDER    = 'pdf_converted';  // converted .dcm files go here
const RECENT_DAYS_LIMIT       = 4;                // fallback scan: files modified within N days
const RECENT_FILES_LIMIT      = 10;               // fallback scan: max results
const APP_NAME                = 'DICOM PDF Gen';

//////////////////////////////////////////////////////////////////////////
// NOTIFICATION HELPER

/**
 * Shows a native OS desktop notification (toast on Windows, notification
 * center on macOS, libnotify on Linux). Falls back to console if unavailable.
 */
function notify(title, message, isError = false) {
  try {
    notifier.notify({
      title,
      message,
      icon: undefined,       // use default system icon
      sound: isError,        // play sound only on errors
      wait: false
    });
  } catch {
    // node-notifier is optional — never crash the main flow
  }
  // Always echo to console as well
  const prefix = isError ? '❌ ' : 'ℹ️  ';
  console.log(`\n${prefix}[${title}] ${message}`);
}

//////////////////////////////////////////////////////////////////////////
// PATH HELPERS

/**
 * Returns the OS "Documents" folder in a cross-platform way.
 * On Windows also checks USERPROFILE in case homedir() resolves differently.
 */
function getDocumentsFolder() {
  if (process.platform === 'win32') {
    // Prefer the registry-backed known-folder via env var if set
    const userProfile = process.env.USERPROFILE || os.homedir();
    return path.join(userProfile, 'Documents');
  }
  return path.join(os.homedir(), 'Documents');
}

/**
 * Returns (and creates if needed) the dedicated PDF drop folder.
 */
function ensurePdfFolder(docsFolder) {
  const pdfFolder = path.join(docsFolder, PDF_SUBFOLDER_NAME);
  if (!fs.existsSync(pdfFolder)) {
    fs.mkdirSync(pdfFolder, { recursive: true });
  }
  return pdfFolder;
}

/**
 * Returns (and creates if needed) the converted-output folder
 * (sibling of the dcm_pdfs folder, inside Documents).
 */
function ensureConvertedFolder(docsFolder) {
  const convertedFolder = path.join(docsFolder, PDF_CONVERTED_FOLDER);
  if (!fs.existsSync(convertedFolder)) {
    fs.mkdirSync(convertedFolder, { recursive: true });
  }
  return convertedFolder;
}

//////////////////////////////////////////////////////////////////////////
// PDF DISCOVERY

/**
 * Lists all PDFs inside the dedicated folder (non-recursive, immediate files only).
 */
function listDedicatedFolderPdfs(pdfFolder) {
  try {
    return fs.readdirSync(pdfFolder)
      .filter(f => path.extname(f).toLowerCase() === '.pdf')
      .map(f => path.join(pdfFolder, f))
      .sort((a, b) => {
        // newest first
        return fs.statSync(b).mtimeMs - fs.statSync(a).mtimeMs;
      });
  } catch {
    return [];
  }
}

/**
 * Scans the Documents folder (non-recursive) for PDFs modified within
 * RECENT_DAYS_LIMIT days, capped at RECENT_FILES_LIMIT results.
 * Uses synchronous glob for simplicity and predictable speed.
 */
function listRecentDocumentsPdfs(docsFolder) {
  const cutoff = Date.now() - RECENT_DAYS_LIMIT * 24 * 60 * 60 * 1000;

  let files = [];
  try {
    // Non-recursive: only top-level Documents files
    files = glob.sync('*.pdf', {
      cwd: docsFolder,
      nocase: true,
      absolute: true
    });
  } catch {
    return [];
  }

  return files
    .map(f => {
      try {
        const stat = fs.statSync(f);
        return { file: f, mtime: stat.mtimeMs };
      } catch {
        return null;
      }
    })
    .filter(entry => entry && entry.mtime >= cutoff)
    .sort((a, b) => b.mtime - a.mtime)
    .slice(0, RECENT_FILES_LIMIT)
    .map(entry => entry.file);
}

//////////////////////////////////////////////////////////////////////////
// INTERACTIVE FILE PICKER

async function pickPdfFile() {
  const docsFolder = getDocumentsFolder();
  const pdfFolder  = ensurePdfFolder(docsFolder);

  // --- 1. Show dedicated folder status ---
  console.log('\n──────────────────────────────────────────');
  console.log(`📁  Dedicated PDF folder: ${pdfFolder}`);

  const dedicatedFiles = listDedicatedFolderPdfs(pdfFolder);

  if (dedicatedFiles.length === 0) {
    console.log('    (empty — copy your PDF files here for quick access)');
  } else {
    console.log(`    ${dedicatedFiles.length} file(s) found`);
  }
  console.log('──────────────────────────────────────────\n');

  // --- 2. Build choice list ---
  const choices = [];

  // Section: dedicated folder
  if (dedicatedFiles.length > 0) {
    choices.push({ name: `── 📂 ${PDF_SUBFOLDER_NAME}/ ──`, value: '__hdr_dedicated__', disabled: true });
    dedicatedFiles.forEach(f => {
      const stat = fs.statSync(f);
      const age  = moment(stat.mtime).fromNow();
      choices.push({
        name:  `  ${path.basename(f)}  (${age})`,
        value: f
      });
    });
  }

  // Section: recent Documents files
  const recentFiles = listRecentDocumentsPdfs(docsFolder).filter(
    f => !dedicatedFiles.includes(f)   // avoid duplicates
  );

  if (recentFiles.length > 0) {
    choices.push({ name: `── 🕐 Recent (last ${RECENT_DAYS_LIMIT} days) ──`, value: '__hdr_recent__', disabled: true });
    recentFiles.forEach(f => {
      const stat = fs.statSync(f);
      const age  = moment(stat.mtime).fromNow();
      choices.push({
        name:  `  ${path.basename(f)}  (${age})`,
        value: f
      });
    });
  }

  // Always-available options
  choices.push({ name: '──────────────────────', value: '__sep__', disabled: true });
  choices.push({ name: '📂  Browse — enter a custom file path', value: '__manual__'  });
  choices.push({ name: '📋  Open dedicated folder in file manager', value: '__open__' });

  if (choices.filter(c => !c.disabled).length === 2) {
    // Only manual + open-folder — no PDFs found anywhere
    notify(
      APP_NAME,
      `No PDF files found in the last ${RECENT_DAYS_LIMIT} days.\nPlace PDFs in: ${pdfFolder}`,
      true
    );
  }

  const { selectedFile } = await inquirer.prompt([{
    type:    'list',
    name:    'selectedFile',
    message: 'Select a PDF file:',
    choices,
    pageSize: 20
  }]);

  // --- 3. Handle special actions ---

  if (selectedFile === '__open__') {
    openFolder(pdfFolder);
    console.log(`\nOpened: ${pdfFolder}`);
    console.log('Place your PDF file(s) there, then re-run the script.\n');
    process.exit(0);
  }

  if (selectedFile === '__manual__') {
    const { manualPath } = await inquirer.prompt([{
      type:     'input',
      name:     'manualPath',
      message:  'Enter the full path to the PDF file:',
      validate: input => {
        const p = input.trim();
        if (!p)                                          return 'Path cannot be empty.';
        if (!fs.existsSync(p))                          return `File not found: ${p}`;
        if (path.extname(p).toLowerCase() !== '.pdf')   return 'File must be a .pdf';
        return true;
      }
    }]);
    return manualPath.trim();
  }

  return selectedFile;
}

/**
 * Opens a folder in the native file manager — cross-platform.
 */
function openFolder(folderPath) {
  const { execSync } = require('child_process');
  try {
    if (process.platform === 'win32') {
      execSync(`explorer "${folderPath}"`);
    } else if (process.platform === 'darwin') {
      execSync(`open "${folderPath}"`);
    } else {
      execSync(`xdg-open "${folderPath}"`);
    }
  } catch {
    // Non-fatal — user still sees the path printed
  }
}

//////////////////////////////////////////////////////////////////////////
// DATA HELPERS

function generateFallbackData(input) {
  return {
    patientId: input.patientId ||
      faker.random.alphaNumeric(8).toUpperCase(),
    patientName: input.patientName ||
      `${faker.name.lastName().toUpperCase()}^${faker.name.firstName().toUpperCase()}`,
    patientBirthDate: input.patientBirthDate ||
      moment(faker.date.between('1950-01-01', '2010-01-01')).format('YYYYMMDD'),
    accessionNumber: input.accessionNumber ||
      faker.random.alphaNumeric(10).toUpperCase()
  };
}

//////////////////////////////////////////////////////////////////////////
// DICOM DATASET GENERATION

function generatePdfDataset(pdfBuffer, data) {
  const date = moment().format('YYYYMMDD');
  const time = moment().format('HHmmss');

  const studyInstanceUid  = DicomMetaDictionary.uid();
  const seriesInstanceUid = DicomMetaDictionary.uid();
  const sopInstanceUid    = DicomMetaDictionary.uid();

  // DICOM requires even-length values
  if (pdfBuffer.length % 2 !== 0) {
    pdfBuffer = Buffer.concat([pdfBuffer, Buffer.from([0x00])]);
  }

  const dataset = {
    _meta: {
      FileMetaInformationVersion:  new Uint8Array([0, 1]).buffer,
      MediaStorageSOPClassUID:     EncapsulatedPdfSopClassUid,
      MediaStorageSOPInstanceUID:  sopInstanceUid,
      TransferSyntaxUID:           ExplicitVrLittleEndianTransferSyntaxUid,
      ImplementationClassUID:      ImplementationUid
    },

    PatientID:        data.patientId,
    PatientName:      data.patientName,
    PatientBirthDate: data.patientBirthDate,
    PatientSex:       'O',

    StudyInstanceUID: studyInstanceUid,
    StudyDate:        date,
    StudyTime:        time,
    AccessionNumber:  data.accessionNumber,

    Modality:         'OT',
    SeriesInstanceUID: seriesInstanceUid,
    SeriesNumber:     '1',

    Manufacturer:     'NODEJS',
    ConversionType:   'WSD',

    InstanceNumber:   '1',
    ContentDate:      date,
    ContentTime:      time,
    MIMETypeOfEncapsulatedDocument: 'application/pdf',
    EncapsulatedDocument:           [pdfBuffer.buffer],

    SOPClassUID:      EncapsulatedPdfSopClassUid,
    SOPInstanceUID:   sopInstanceUid
  };

  const meta     = DicomMetaDictionary.denaturalizeDataset(dataset._meta);
  const dicomDict = new DicomDict(meta);
  dicomDict.dict  = DicomMetaDictionary.denaturalizeDataset(dataset);

  return Buffer.from(dicomDict.write());
}

//////////////////////////////////////////////////////////////////////////
// DICOM SEND

function sendToDicomNode(filePath, config) {
  return new Promise((resolve, reject) => {
    const client = new dimse.Client();
    client.send(
      config.ip,
      config.port,
      config.calledAET,
      config.callingAET,
      filePath,
      err => (err ? reject(err) : resolve())
    );
  });
}

//////////////////////////////////////////////////////////////////////////
// MAIN

async function main() {
  try {
    const docsFolder      = getDocumentsFolder();
    const convertedFolder = ensureConvertedFolder(docsFolder);

    // Honour PDF_FILE env var for scripted / CI usage
    let pdfFile = process.env.PDF_FILE || null;

    if (pdfFile) {
      console.log(`PDF_FILE env var set — using: ${pdfFile}`);
    } else {
      pdfFile = await pickPdfFile();
    }

    console.log(`\n✅  Using PDF: ${pdfFile}`);

    const pdfBuffer = fs.readFileSync(pdfFile);

    // Basic PDF magic-byte validation (covers PDF 1.0–1.7 and 2.0)
    if (!/^%PDF-[12]\.[0-9]/.test(pdfBuffer.slice(0, 10).toString('ascii'))) {
      throw new Error('File does not appear to be a valid PDF.');
    }

    // Gather patient / study metadata
    const answers = await inquirer.prompt([
      { name: 'patientId',        message: 'Patient ID         (empty = auto-generate):' },
      { name: 'patientName',      message: 'Patient Name       (LAST^FIRST, empty = auto):' },
      { name: 'patientBirthDate', message: 'Birth Date         (YYYYMMDD, empty = auto):' },
      { name: 'accessionNumber',  message: 'Accession Number   (empty = auto-generate):' },
      {
        type:    'confirm',
        name:    'sendDicom',
        message: 'Send to DICOM node?',
        default: false
      }
    ]);

    const data = generateFallbackData(answers);

    console.log('\nFinal patient data:');
    console.table(data);

    const dicomBuffer = generatePdfDataset(pdfBuffer, data);

    // ── Write .dcm into pdf_converted folder ──────────────────────────────
    const baseName   = path.basename(pdfFile, path.extname(pdfFile));
    const timestamp  = moment().format('YYYYMMDDHHmmss');
    const dcmName    = `${baseName}_${timestamp}.dcm`;
    const outFile    = path.join(convertedFolder, dcmName);

    fs.writeFileSync(outFile, dicomBuffer);
    console.log(`\n✅  DICOM file written: ${outFile}`);
    notify(APP_NAME, `Conversion complete:\n${dcmName}`);

    // ── Optionally send to DICOM node ─────────────────────────────────────
    if (answers.sendDicom) {
      const config = {
        ip:         '127.0.0.1',
        port:       104,
        calledAET:  'WORK',
        callingAET: 'PDFSCU'
      };

      console.log(`\nSending to ${config.ip}:${config.port} …`);
      await sendToDicomNode(outFile, config);

      // Rename .dcm → .sent to mark successful transmission
      const sentFile = outFile.replace(/\.dcm$/, '.sent');
      fs.renameSync(outFile, sentFile);

      console.log(`✅  Sent and renamed: ${path.basename(sentFile)}`);
      notify(APP_NAME, `File sent successfully:\n${path.basename(sentFile)}`);
    }

  } catch (err) {
    notify(APP_NAME, `Error: ${err.message}`, true);
    console.error(`\n❌  Error: ${err.message}`);
    process.exit(1);
  }
}

main();