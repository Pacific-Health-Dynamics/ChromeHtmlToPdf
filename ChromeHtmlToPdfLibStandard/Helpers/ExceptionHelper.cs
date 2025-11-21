using System;

namespace ChromeHtmlToPdfLib.Helpers
{
    /// <summary>
    ///     Exception helper methods
    /// </summary>
    public static class ExceptionHelpers
    {
        /// <summary>
        ///     Returns the full exception with it's inner exceptions as a string
        /// </summary>
        /// <param name="exception">The exception</param>
        /// <returns></returns>
        public static string GetInnerException(Exception exception)
        {
            var result = string.Empty;

            if (exception == null) return result;
            result = exception.Message + Environment.NewLine;
            if (exception.InnerException != null)
                result += GetInnerException(exception.InnerException);
            return result;
        }
    }
}