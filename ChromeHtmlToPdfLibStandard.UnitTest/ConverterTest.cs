using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ChromeHtmlToPdfLib;
using ChromeHtmlToPdfLib.Settings;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace ChromeHtmlToPdfLibStandard.UnitTest;

public class ConverterTest
{
    private readonly string _htmlFileContent;

    private readonly ITestOutputHelper _output;
    private readonly string _textFileContent;
    private readonly string _xmlFileContent;

    private ConverterTest()
    {
    }

    public ConverterTest(ITestOutputHelper output)
    {
        _output = output;
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var testFileDir = Path.Combine(baseDir, "TestFiles");

        _htmlFileContent = File.ReadAllText(Path.Combine(testFileDir, "Test.html"), Encoding.Default);
        _xmlFileContent = File.ReadAllText(Path.Combine(testFileDir, "Test.xml"), Encoding.Default);
        _textFileContent = File.ReadAllText(Path.Combine(testFileDir, "Test.txt"), Encoding.Default);
    }

    [Fact]
    public async Task TestHtml()
    {
        using var chrome = new ChromeProcess();
        chrome.EnsureRunning();
        var infile = Guid.NewGuid() + ".html";
        infile = Path.Combine(Path.GetTempPath(), infile);
        await File.WriteAllTextAsync(infile, _htmlFileContent);

        await ConvertWithProcessAsync(chrome, infile);
    }

    [Fact]
    public async Task TestText()
    {
        using var chrome = new ChromeProcess();
        chrome.EnsureRunning();
        var infile = Guid.NewGuid() + ".txt";
        infile = Path.Combine(Path.GetTempPath(), infile);
        await File.WriteAllTextAsync(infile, _textFileContent);
        await ConvertWithProcessAsync(chrome, infile);
    }

    [Fact]
    public async Task TestXml()
    {
        using var chrome = new ChromeProcess();
        chrome.EnsureRunning();

        var infile = Guid.NewGuid() + ".xml";
        infile = Path.Combine(Path.GetTempPath(), infile);
        await File.WriteAllTextAsync(infile, _xmlFileContent);

        await ConvertWithProcessAsync(chrome, infile);
    }

    [Fact]
    public async Task ThreadingStressTest()
    {
        using var chrome = new ChromeProcess();
        chrome.EnsureRunning();

        var source = new CancellationTokenSource();
        source.CancelAfter(TimeSpan.FromMinutes(3));

        var infile = Guid.NewGuid() + ".xml";
        infile = Path.Combine(Path.GetTempPath(), infile);
        await File.WriteAllTextAsync(infile, _xmlFileContent, source.Token);
        var tasks = new List<Task>();
        for (var i = 0; i < 100; i++) tasks.Add(ConvertWithProcessAsync(chrome, infile, source.Token));

        Task.WaitAll(tasks.ToArray(), source.Token);
    }

    [Fact]
    public async Task StressTest()
    {
        using var chrome = new ChromeProcess();
        chrome.EnsureRunning();

        var infile = Guid.NewGuid() + ".xml";
        infile = Path.Combine(Path.GetTempPath(), infile);
        await File.WriteAllTextAsync(infile, _xmlFileContent);
        for (var i = 0; i < 100; i++) await ConvertWithProcessAsync(chrome, infile);
    }

    private async Task ConvertWithProcessAsync(ChromeProcess process, string infile,
        CancellationToken cancellationToken = default)
    {
        var pageSettings = new PageSettings();

        using var memoryStream = new MemoryStream();
        using var converter = new Converter(process, new ConsoleLogger(_output));
        await converter.ConvertToPdfAsync(new ConvertUri(infile), memoryStream, pageSettings, cancellationToken);
        //Some random non empty bytes
        if (memoryStream.Length < 1000)
            throw new Exception("HTML to PDF conversion failed; No result");
    }

    private class ConsoleLogger : ILogger, IDisposable
    {
        private readonly ITestOutputHelper _output;

        public ConsoleLogger(ITestOutputHelper output)
        {
            _output = output;
        }

        public void Dispose()
        {
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception,
            Func<TState, Exception, string> formatter)
        {
            Console.WriteLine($"Converter: {formatter(state, exception)}");
            _output.WriteLine($"Converter: {formatter(state, exception)}");
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public IDisposable BeginScope<TState>(TState state)
        {
            return this;
        }
    }
}