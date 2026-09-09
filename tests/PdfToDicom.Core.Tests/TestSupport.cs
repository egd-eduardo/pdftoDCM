using PdfToDicom.Core.Abstractions;
using PdfToDicom.Core.Configuration;
using PdfToDicom.Core.Dicom;
using PdfToDicom.Core.Models;
using PdfToDicom.Core.Pdf;

namespace PdfToDicom.Core.Tests;

/// <summary>
/// A logger that records entries in memory, so tests can assert on what was logged
/// without touching the file system.
/// </summary>
internal sealed class RecordingLogger : IAppLogger
{
    public List<string> Messages { get; } = new();

    public List<string> Errors { get; } = new();

    public string LogFolder => Path.GetTempPath();

    public void Debug(string message) => Messages.Add("DEBUG " + message);

    public void Information(string message) => Messages.Add("INFO " + message);

    public void Warning(string message) => Messages.Add("WARN " + message);

    public void Error(string message, Exception? exception = null)
    {
        Messages.Add("ERROR " + message);
        Errors.Add(message);
    }

    public void Flush()
    {
    }
}

/// <summary>
/// Shared helpers for building the objects under test.
/// </summary>
internal static class TestSupport
{
    /// <summary>Path of the sample PDF copied into the test output folder.</summary>
    public static string SamplePdfPath =>
        Path.Combine(AppContext.BaseDirectory, "example-report.pdf");

    /// <summary>A configuration with predictable values for assertions.</summary>
    public static AppConfiguration CreateConfiguration()
    {
        AppConfiguration configuration = ConfigurationService.CreateDefaults();

        configuration.InstitutionName = "Test Hospital";
        configuration.DepartmentName = "Radiology";
        configuration.StationName = "TESTSTATION";
        configuration.CallingAeTitle = "PDF2DCM";
        configuration.Manufacturer = "PDF to DICOM";
        configuration.OutputFolder = Path.Combine(Path.GetTempPath(), "PdfToDicomTests", Guid.NewGuid().ToString("N"));
        configuration.LogFolder = configuration.OutputFolder;

        return configuration;
    }

    /// <summary>Valid demographics, used as the starting point for negative cases.</summary>
    public static PatientDemographics CreateDemographics() => new()
    {
        PatientId = "PID000123",
        FamilyName = "Silva",
        GivenName = "Maria",
        DateOfBirth = new DateTime(1970, 5, 21),
        Sex = PatientSex.Female,
        AccessionNumber = "ACC0001"
    };

    /// <summary>Builds an object from the sample PDF using the supplied configuration.</summary>
    public static BuiltDicomObject BuildSampleObject(
        AppConfiguration? configuration = null,
        PatientDemographics? demographics = null)
    {
        configuration ??= CreateConfiguration();
        demographics ??= CreateDemographics();

        var logger = new RecordingLogger();
        var inspector = new PdfInspector(logger);
        var builder = new EncapsulatedPdfBuilder(
            configuration,
            UidGenerator.FromConfiguration(configuration),
            inspector,
            logger);

        return builder.Build(SamplePdfPath, demographics);
    }
}
