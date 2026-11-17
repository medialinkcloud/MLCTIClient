using System.Globalization;

namespace MedialinkCTIClient;

/// <summary>
/// Presentation-only Rubrica sorting. The search results and their original
/// contact metadata are untouched; row.Tag always points to the exact contact.
/// </summary>
internal static class ContactSortRules
{
    private static readonly StringComparer ContactComparer =
        StringComparer.Create(CultureInfo.GetCultureInfo("it-IT"), ignoreCase: true);

    internal static IEnumerable<Contact> Order(IEnumerable<Contact> contacts, string? column, bool ascending) =>
        column switch
        {
            "name" => ascending
                ? contacts.OrderBy(c => c.Name, ContactComparer).ThenBy(c => c.Number, StringComparer.Ordinal)
                : contacts.OrderByDescending(c => c.Name, ContactComparer).ThenBy(c => c.Number, StringComparer.Ordinal),
            "phone" => ascending
                ? contacts.OrderBy(c => PhoneNumbers.Dialable(c.Number), StringComparer.Ordinal)
                    .ThenBy(c => c.Name, ContactComparer)
                : contacts.OrderByDescending(c => PhoneNumbers.Dialable(c.Number), StringComparer.Ordinal)
                    .ThenBy(c => c.Name, ContactComparer),
            _ => contacts
        };

    internal static bool SelfTest()
    {
        var a = new Contact("Zeta", "200", "");
        var b = new Contact("Alfa", "300", "");
        var c = new Contact("Beta", "100", "");
        var all = new[] { a, b, c };
        return Order(all, "name", true).SequenceEqual(new[] { b, c, a }) &&
               Order(all, "name", false).SequenceEqual(new[] { a, c, b }) &&
               Order(all, "phone", true).SequenceEqual(new[] { c, a, b }) &&
               Order(all, "phone", false).SequenceEqual(new[] { b, a, c }) &&
               Order(all, null, true).SequenceEqual(all);
    }
}
