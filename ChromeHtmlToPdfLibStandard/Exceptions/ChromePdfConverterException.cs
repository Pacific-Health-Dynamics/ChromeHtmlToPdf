using System;
using System.Runtime.Serialization;

namespace ChromeHtmlToPdfLib.Exceptions
{
    public abstract class ChromePdfConverterException : Exception
    {
        protected ChromePdfConverterException()
        {
        }

        protected ChromePdfConverterException(string message) : base(message)
        {
        }

        protected ChromePdfConverterException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }
}