using PdfToDicom.Interop.Messaging;

namespace PdfToDicom.Integration.Configuration;

/// <summary>
/// The invented patient used to commission an interface.
/// </summary>
/// <remarks>
/// <para>
/// One definition, used by the command line and by the test message the settings page
/// sends. Two definitions would eventually disagree, and the one that matters is the one
/// somebody at the receiving end is looking for.
/// </para>
/// <para>
/// The values are deliberately absurd. A test message does sometimes escape into a real
/// system during commissioning, and when it does, whoever finds it should be able to tell
/// at a glance that it is a test rather than a patient nobody can trace. ZZZTEST sorts to
/// the end of an alphabetical list, which is where somebody will notice it.
/// </para>
/// </remarks>
public static class Hl7TestSubject
{
    /// <summary>The family name every test message carries.</summary>
    public const string FamilyName = "ZZZTEST";

    /// <summary>Builds the subject, optionally for a named destination.</summary>
    /// <param name="destination">
    /// The destination whose four names go into the header, or null to leave them empty.
    /// </param>
    /// <param name="now">The moment, or the current one.</param>
    public static MessageSubject Create(
        Hl7DestinationSettings? destination = null, DateTimeOffset? now = null)
    {
        DateTimeOffset moment = now ?? DateTimeOffset.Now;

        var subject = new MessageSubject()
            .With("now", moment.ToString("o"))
            .With("today", moment.ToString("yyyy-MM-dd"))

            .With("patient.id", "TEST0000001")
            .With("patient.familyName", FamilyName)
            .With("patient.givenName", "Donotuse")
            .With("patient.dateOfBirth", "1970-05-21T00:00:00")
            .With("patient.sex", "Other")
            .With("patient.sexCode", "O")

            .With("study.accessionNumber", "TESTACC001")
            .With("study.description", "Interface test, not a real study")
            .With("study.dateTime", moment.ToString("o"))

            // A UID under the application's own test branch, so it cannot collide with a
            // real study even if a receiver stores it.
            .With("study.instanceUid", "1.2.826.0.1.3680043.8.498.99999999999999999999")
            .With("document.title", "Interface test")
            .With("document.instanceCount", "1");

        if (destination is not null)
        {
            subject = subject
                .With("destination.name", destination.Name)
                .With("destination.sendingApplication", destination.SendingApplication)
                .With("destination.sendingFacility", destination.SendingFacility)
                .With("destination.receivingApplication", destination.ReceivingApplication)
                .With("destination.receivingFacility", destination.ReceivingFacility);
        }

        return subject;
    }
}
