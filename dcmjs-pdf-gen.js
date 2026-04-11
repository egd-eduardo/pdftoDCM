// dcmjs-pdf-gen
// This Node.js utility can generate DICOM Encapsulated PDF datasets,
// for testing purposes.

// The following npm packages are required:
// dcmjs
// faker
// moment
// e.g. npm install dcmjs faker@5.5.3 moment

// Usage:
// [OPTIONS] node dcmjs-pdf-gen.js
// [OPTIONS]:
// PDF_FILE: The file path of the PDF document to encapsulate.
// OUTPUT_FILE: Output file path (default: pdf.dcm).
// e.g. PDF_FILE=test.pdf node dcmjs-pdf-gen.js

//added possibility to dicom send to dicom destination
const fs = require('fs');
const inquirer = require('inquirer');
const faker = require('faker');
const moment = require('moment');
const dcmjs = require('dcmjs');
const dimse = require('dicom-dimse-native');

const { DicomMetaDictionary, DicomDict } = dcmjs.data;

//////////////////////////////////////////////////////////////////////////
// CONSTANTS

const EncapsulatedPdfSopClassUid = '1.2.840.10008.5.1.4.1.1.104.1';
const ExplicitVrLittleEndianTransferSyntaxUid = '1.2.840.10008.1.2.1';
const ImplementationUid = '1.3.6.1.4.1.30071.8';

//////////////////////////////////////////////////////////////////////////
// UTIL FUNCTIONS

function generateFallbackData(input) {
  return {
    patientId: input.patientId || faker.random.alphaNumeric(8).toUpperCase(),
    patientName:
      input.patientName ||
      `${faker.name.lastName().toUpperCase()}^${faker.name.firstName().toUpperCase()}`,
    patientBirthDate:
      input.patientBirthDate ||
      moment(faker.date.between('1950-01-01', '2010-01-01')).format('YYYYMMDD'),
    accessionNumber:
      input.accessionNumber || faker.random.alphaNumeric(10).toUpperCase()
  };
}

//////////////////////////////////////////////////////////////////////////
// DICOM DATASET GENERATION

function generatePdfDataset(pdfBuffer, data) {
  const date = moment().format('YYYYMMDD');
  const time = moment().format('HHmmss');

  const studyInstanceUid = DicomMetaDictionary.uid();
  const seriesInstanceUid = DicomMetaDictionary.uid();
  const sopInstanceUid = DicomMetaDictionary.uid();

  if (pdfBuffer.length % 2 !== 0) {
    pdfBuffer = Buffer.concat([pdfBuffer, Buffer.from([0x00])]);
  }

  const dataset = {
    _meta: {
      FileMetaInformationVersion: new Uint8Array([0, 1]).buffer,
      MediaStorageSOPClassUID: EncapsulatedPdfSopClassUid,
      MediaStorageSOPInstanceUID: sopInstanceUid,
      TransferSyntaxUID: ExplicitVrLittleEndianTransferSyntaxUid,
      ImplementationClassUID: ImplementationUid
    },

    PatientID: data.patientId,
    PatientName: data.patientName,
    PatientBirthDate: data.patientBirthDate,
    PatientSex: 'O',

    StudyInstanceUID: studyInstanceUid,
    StudyDate: date,
    StudyTime: time,
    AccessionNumber: data.accessionNumber,

    Modality: 'OT',
    SeriesInstanceUID: seriesInstanceUid,
    SeriesNumber: '1',

    Manufacturer: 'NODEJS',
    ConversionType: 'WSD',

    InstanceNumber: '1',
    ContentDate: date,
    ContentTime: time,
    MIMETypeOfEncapsulatedDocument: 'application/pdf',
    EncapsulatedDocument: [pdfBuffer.buffer],

    SOPClassUID: EncapsulatedPdfSopClassUid,
    SOPInstanceUID: sopInstanceUid
  };

  const meta = DicomMetaDictionary.denaturalizeDataset(dataset._meta);
  const dicomDict = new DicomDict(meta);
  dicomDict.dict = DicomMetaDictionary.denaturalizeDataset(dataset);

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
      (err) => {
        if (err) return reject(err);
        resolve();
      }
    );
  });
}

//////////////////////////////////////////////////////////////////////////
// MAIN FLOW

async function main() {
  try {
    const pdfFile = process.env.PDF_FILE;
    if (!pdfFile) {
      throw new Error('PDF_FILE environment variable is required');
    }

    const pdfBuffer = fs.readFileSync(pdfFile);

    // Validate PDF
    if (!pdfBuffer.slice(0, 8).toString().match(/%PDF-1.[0-7]/)) {
      throw new Error('Invalid PDF file');
    }

    // PROMPT USER
    const answers = await inquirer.prompt([
      { name: 'patientId', message: 'Patient ID (leave empty to auto-generate):' },
      { name: 'patientName', message: 'Patient Name (DOE^JOHN):' },
      { name: 'patientBirthDate', message: 'Birth Date (YYYYMMDD):' },
      { name: 'accessionNumber', message: 'Accession Number:' },
      {
        type: 'confirm',
        name: 'sendDicom',
        message: 'Send to DICOM node?',
        default: false
      }
    ]);

    const data = generateFallbackData(answers);

    console.log('\nFinal Data Used:');
    console.log(data);

    const dicomBuffer = generatePdfDataset(pdfBuffer, data);

    const outFile = 'output.dcm';
    fs.writeFileSync(outFile, dicomBuffer);

    console.log(`DICOM file created: ${outFile}`);

    // OPTIONAL SEND
    if (answers.sendDicom) {
      const config = {
        ip: '127.0.0.1',
        port: 104,
        calledAET: 'WORK',
        callingAET: 'PDFSCU'
      };

      await sendToDicomNode(outFile, config);
      console.log('DICOM file sent successfully');
    }

  } catch (err) {
    console.error('Error:', err.message);
  }
}

main();