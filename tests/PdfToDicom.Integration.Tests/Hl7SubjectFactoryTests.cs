using PdfToDicom.Core.Models;
using PdfToDicom.Integration;
using PdfToDicom.Interop.Messaging;
using Xunit;

namespace PdfToDicom.Integration.Tests;

public sealed class Hl7SubjectFactoryTests
{
    private static readonly DateTimeOffset Moment =
        new(2026, 11, 15, 9, 30, 0, TimeSpan.Zero);

    private static PatientDemographics Complete() => new()
    {
        PatientId = "0000000077",
        FamilyName = "Silva",
        GivenName = "Maria",
        MiddleName = "C",
        DateOfBirth = new DateTime(1970, 5, 21),
        Sex = PatientSex.Female,
        AccessionNumber = "ACC001",
        StudyDescription = "Chest radiograph",
        SeriesDescription = "Report",
        DocumentTitle = "Discharge note",
        ReferringPhysicianName = "Costa^Ana",
        StudyId = "ST1"
    };

    [Fact]
    public void EverythingTheOperatorEnteredIsAvailableToAProfile()
    {
        MessageSubject subject = Hl7SubjectFactory.From(Complete(), null, Moment);

        Assert.Equal("0000000077", subject.Get("patient.id"));
        Assert.Equal("Silva", subject.Get("patient.familyName"));
        Assert.Equal("Maria", subject.Get("patient.givenName"));
        Assert.Equal("C", subject.Get("patient.middleName"));
        Assert.Equal("ACC001", subject.Get("study.accessionNumber"));
        Assert.Equal("Chest radiograph", subject.Get("study.description"));
        Assert.Equal("Costa^Ana", subject.Get("referring.physician"));
        Assert.Equal("Discharge note", subject.Get("document.title"));
    }

    [Fact]
    public void SexIsOfferedBothAsAWordAndAsTheCodeTheDicomObjectCarries()
    {
        // A profile may map either: the word suits a code table written in words, and the
        // code is the same letter the DICOM object holds, so the two systems agree.
        MessageSubject subject = Hl7SubjectFactory.From(Complete(), null, Moment);

        Assert.Equal("Female", subject.Get("patient.sex"));
        Assert.Equal("F", subject.Get("patient.sexCode"));
    }

    [Fact]
    public void AnUnspecifiedSexIsEmptyRatherThanGuessed()
    {
        PatientDemographics unknown = Complete();
        unknown.Sex = PatientSex.Unspecified;

        MessageSubject subject = Hl7SubjectFactory.From(unknown, null, Moment);

        Assert.Equal(string.Empty, subject.Get("patient.sexCode"));
    }

    [Fact]
    public void AnAbsentDateOfBirthIsEmptyAndNeverToday()
    {
        // The same rule the DICOM side follows for a Type 2 attribute, and for the same
        // reason: a plausible value nobody supplied is worse than an obvious gap.
        PatientDemographics without = Complete();
        without.DateOfBirth = null;

        Assert.Equal(string.Empty, Hl7SubjectFactory.From(without, null, Moment).Get("patient.dateOfBirth"));
    }

    [Fact]
    public void DatesAreOfferedInIsoSoTheProfileDecidesTheFormat()
    {
        // One destination wants eight digits and the next wants fourteen. Formatting here
        // would make that a code change rather than a mapping.
        MessageSubject subject = Hl7SubjectFactory.From(Complete(), null, Moment);

        Assert.Equal("1970-05-21T00:00:00", subject.Get("patient.dateOfBirth"));
        Assert.StartsWith("2026-11-15", subject.Get("now"), StringComparison.Ordinal);
        Assert.Equal("2026-11-15", subject.Get("today"));
    }

    [Fact]
    public void WhatTheConversionProducedIsAddedWhenThereIsSome()
    {
        var result = new ConversionResult
        {
            DicomGenerated = true,
            StudyInstanceUid = "1.2.3.4",
            SeriesInstanceUid = "1.2.3.5",
            SopInstanceUid = "1.2.3.6",
            SopInstanceUids = ["1.2.3.6", "1.2.3.7"],
            GeneratedFilePath = "/tmp/report.dcm"
        };

        MessageSubject subject = Hl7SubjectFactory.From(Complete(), result, Moment);

        Assert.Equal("1.2.3.4", subject.Get("study.instanceUid"));
        Assert.Equal("1.2.3.5", subject.Get("series.instanceUid"));
        Assert.Equal("1.2.3.6", subject.Get("document.sopInstanceUid"));
        Assert.Equal("2", subject.Get("document.instanceCount"));
    }

    [Fact]
    public void WithNoConversionYetTheIdentifiersAreSimplyAbsent()
    {
        // The message first sequence builds a subject before anything has been converted.
        MessageSubject subject = Hl7SubjectFactory.From(Complete(), null, Moment);

        Assert.Equal(string.Empty, subject.Get("study.instanceUid"));
        Assert.Equal(string.Empty, subject.Get("document.sopInstanceUid"));
    }

    [Fact]
    public void AnythingNobodySuppliedReadsAsEmptyRatherThanThrowing()
    {
        MessageSubject subject = Hl7SubjectFactory.From(new PatientDemographics(), null, Moment);

        Assert.Equal(string.Empty, subject.Get("patient.id"));
        Assert.Equal(string.Empty, subject.Get("nothing.like.this"));
    }
}
