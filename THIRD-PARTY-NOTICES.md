# Third Party Notices

PDF to DICOM incorporates the following third party components. Each remains subject to its
own licence, which is unaffected by the proprietary licence governing PDF to DICOM itself.

All three are permissive licences that allow use in proprietary, commercially distributed
software without any obligation to publish your own source code. None imposes a copyleft
requirement.

---

## fo-dicom (Fellow Oak DICOM) 5.2.2

DICOM encoding and network services.

- Project: https://github.com/fo-dicom/fo-dicom
- Licence: **Microsoft Public Licence (MS-PL)**

The MS-PL grants royalty free rights to reproduce and distribute the software and
derivative works, including in a commercial product, provided that:

- the licence notice is retained;
- no trademark licence is implied;
- the software is distributed without warranty;
- if distributed in source form, the MS-PL terms are included.

Distributing fo-dicom in compiled form inside a proprietary application is expressly
permitted, and is what this application does.

Full text: https://opensource.org/licenses/MS-PL

---

## PdfPig 0.1.10

Fully managed PDF parsing, used to read page counts, document titles and to confirm a
selected file is a readable PDF.

- Project: https://github.com/UglyToad/PdfPig
- Licence: **MIT Licence**

```
Permission is hereby granted, free of charge, to any person obtaining a copy of this
software and associated documentation files (the "Software"), to deal in the Software
without restriction, including without limitation the rights to use, copy, modify, merge,
publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or
substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
DEALINGS IN THE SOFTWARE.
```

PdfPig itself incorporates portions of PDFBox, under the Apache Licence 2.0.

---

## Serilog 4.2.0 and Serilog.Sinks.File 6.0.0

Structured diagnostic logging with daily rolling files.

- Project: https://serilog.net/
- Licence: **Apache Licence 2.0**

The Apache Licence 2.0 permits use, reproduction and distribution in source or object form,
including within a proprietary product, provided that:

- a copy of the licence is included;
- modified files are marked as changed;
- attribution notices are retained;
- any `NOTICE` file content is reproduced.

It also grants an express patent licence, which is one reason it is well regarded for
commercial use.

Full text: https://www.apache.org/licenses/LICENSE-2.0

---

## SixLabors.ImageSharp 2.1.11

Decodes JPEG, PNG, TIFF, BMP, GIF, WebP and TGA files so they can be converted into DICOM
Secondary Capture image objects.

- Project: https://github.com/SixLabors/ImageSharp
- Licence: **Apache Licence 2.0**

**The version is pinned deliberately.** ImageSharp changed its licence at version 3.0 to
the Six Labors Split License, which is free only for open source projects and for
organisations below a revenue threshold. The 2.1 series remains under the plain Apache
Licence 2.0, which imposes no such condition and matches every other component here. That
keeps the licence position of this software simple to state and simple for a hospital's
procurement to accept.

It is also pure managed code, with no native libraries.

Full text: https://www.apache.org/licenses/LICENSE-2.0

---

## PDFtoImage 4.1.1, PDFium and SkiaSharp

Renders the pages of a PDF to images, so that the report can also be sent as Secondary
Capture objects. Many PACS viewers will only open a study that contains an image, and an
Encapsulated PDF is not an image SOP Class; without this such a viewer refuses to launch
and the report cannot be read at all.

| Component | Project | Licence |
|---|---|---|
| PDFtoImage | https://github.com/sungaila/PDFtoImage | **MIT Licence** |
| PDFium (via bblanchon.PDFium) | https://pdfium.googlesource.com/pdfium/ | **Apache Licence 2.0** |
| SkiaSharp | https://github.com/mono/SkiaSharp | **MIT Licence** |

PDFium is the PDF engine used by Google Chrome, so the rendering path is the same one
billions of documents pass through daily.

**These are the only native binaries in the product.** Everything else is managed code.
They were accepted after the alternatives were considered: Ghostscript and MuPDF are both
AGPL, which would place conditions on every hospital receiving this software, and there is
no pure managed PDF rasterizer of production quality under a permissive licence. All three
components ship prebuilt for win-x86, win-x64 and win-arm64, so every architecture this
software targets is covered.

Full texts: https://www.apache.org/licenses/LICENSE-2.0 and https://opensource.org/licenses/MIT

---

## Microsoft .NET 8 runtime and WPF

Embedded in the published executable so that no runtime need be installed on the target
workstation.

- Licence: **MIT Licence** (the .NET runtime and libraries)
- Redistribution of the runtime inside a self contained application is expressly permitted
  by Microsoft.

---

## Development and verification only

The following are used during development and continuous integration. **Neither is
distributed with the application**, so their licences impose no obligation on your
distribution.

| Component | Licence | Use |
|---|---|---|
| xUnit | Apache Licence 2.0 | Test framework |
| DCMTK | BSD style (OFFIS) | Reference DICOM implementation used to verify interoperability |
| Inno Setup | Modified BSD | Installer compiler |

---

## Summary for a procurement or legal review

> PDF to DICOM is proprietary software. It embeds four third party components, all under
> permissive licences (MS-PL, MIT and Apache 2.0). None of these licences is copyleft, none
> requires disclosure of PDF to DICOM's own source code, and all permit redistribution in
> compiled form within a commercial product. The only obligations are attribution and
> retention of licence notices, which this document satisfies.
