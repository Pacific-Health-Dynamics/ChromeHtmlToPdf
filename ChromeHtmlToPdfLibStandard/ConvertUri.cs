using System;

namespace ChromeHtmlToPdfLib;

/// <summary>
///     <inheritdoc cref="Uri" />
/// </summary>
public class ConvertUri : Uri
{
    public ConvertUri(string uriString) : base(uriString)
    {
    }
}