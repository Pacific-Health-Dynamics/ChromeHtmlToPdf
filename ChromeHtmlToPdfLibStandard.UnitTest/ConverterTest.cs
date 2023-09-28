using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using ChromeHtmlToPdfLib;
using ChromeHtmlToPdfLib.Settings;
using Xunit;

namespace ChromeHtmlToPdfLibStandard.UnitTest
{
    public class ConverterTest
    {

        private readonly string _htmlFileContent;
        private readonly string _xmlFileContent;
        private readonly string _textFileContent;
        
        public ConverterTest()
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var testFileDir = Path.Combine(baseDir, "TestFiles");

            _htmlFileContent = File.ReadAllText(Path.Combine(testFileDir, "Test.html"), Encoding.Default);
            _xmlFileContent = File.ReadAllText(Path.Combine(testFileDir, "Test.xml"), Encoding.Default);
            _textFileContent = File.ReadAllText(Path.Combine(testFileDir, "Test.txt"), Encoding.Default);

        }
        
        [Fact]
        public async Task TestHtml()
        {
            var infile = Guid.NewGuid() + ".html";
            var outfile = Guid.NewGuid() + ".pdf";
            infile = Path.Combine(Path.GetTempPath(), infile);
            await File.WriteAllTextAsync(infile, _htmlFileContent);

            await ConvertAsync(infile);
        }
        
        [Fact]
        public async Task TestText()
        {
            var infile = Guid.NewGuid() + ".txt";
            infile = Path.Combine(Path.GetTempPath(), infile);
            await File.WriteAllTextAsync(infile, _textFileContent);
            await ConvertAsync(infile);
        }
        
        [Fact]
        public async Task TestXml()
        {
            var infile = Guid.NewGuid() + ".xml";
            var outfile = Guid.NewGuid() + ".pdf";
            infile = Path.Combine(Path.GetTempPath(), infile);
            await File.WriteAllTextAsync(infile, _xmlFileContent);

            await ConvertAsync(infile);
        }

        [Fact]
        public void TreadingStressTest()
        {
            using var chrome = new ChromeProcess();
            chrome.EnsureRunning();
            var infile = Guid.NewGuid() + ".xml";
            infile = Path.Combine(Path.GetTempPath(), infile);
            File.WriteAllText(infile, _xmlFileContent);
            var tasks = new List<Task>();
            for (var i = 0; i < 100; i++)
            {
                tasks.Add(ConvertWithProcessAsync(chrome, infile));
            }

            Task.WaitAll(tasks.ToArray());
        }
        
        [Fact]
        public async Task StressTest()
        {
            using var chrome = new ChromeProcess();
            chrome.EnsureRunning();
            var infile = Guid.NewGuid() + ".xml";
            infile = Path.Combine(Path.GetTempPath(), infile);
            await File.WriteAllTextAsync(infile, _xmlFileContent);
            for (var i = 0; i < 100; i++)
            {
                await ConvertWithProcessAsync(chrome, infile);
            }
        }

        private async Task ConvertWithProcessAsync(ChromeProcess process, string infile)
        {
            var pageSettings = new PageSettings();

            using var memoryStream = new MemoryStream();
            using var converter = new Converter(process);
            await converter.ConvertToPdfAsync(new ConvertUri(infile), memoryStream, pageSettings);
            //Some random non empty bytes
            if (memoryStream.Length < 1000)
                throw new Exception($"HTML to PDF conversion failed; No result");
        }

        private async Task ConvertAsync(string infile)
        {
            using var chrome = new ChromeProcess();
            chrome.EnsureRunning();
            await ConvertWithProcessAsync(chrome, infile);
        }
    }
}