using System;
using System.Runtime.Serialization;

namespace ChromeHtmlToPdfLib.Exceptions
{
    /// <summary>
    ///     Raised when the PDF conversion fails
    /// </summary>
    [Serializable]
    internal sealed class ConversionException : ChromePdfConverterException
    {
        internal ConversionException()
        {
        }

        internal ConversionException(string message) : base(message)
        {
        }

        internal ConversionException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }
}