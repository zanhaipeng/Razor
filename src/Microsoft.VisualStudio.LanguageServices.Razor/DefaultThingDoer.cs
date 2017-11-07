using System.ComponentModel.Composition;
using System.IO;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.VisualStudio.ProjectSystem.Razor;

namespace Microsoft.VisualStudio.LanguageServices.Razor
{
    [Export(typeof(IThingDoer))]
    internal class DefaultThingDoer : IThingDoer
    {
        private readonly RazorTemplateEngineFactoryService _factory;
        private readonly Workspace _workspace;

        [ImportingConstructor]
        public DefaultThingDoer(
            [Import(typeof(VisualStudioWorkspace))] Workspace workspace, RazorTemplateEngineFactoryService factory)
        {
            _factory = factory;
            _workspace = workspace;
        }

        public void DoTheNeedful(object obj, string fullPath, Stream stream)
        {
            var engine = _factory.Create((string)obj, (b) => { });

            var cSharpDocument = engine.GenerateCode(fullPath);

            using (var writer = new StreamWriter(stream, Encoding.UTF8, bufferSize: 1024, leaveOpen: true))
            {
                writer.Write(cSharpDocument.GeneratedCode);
            }
        }
    }
}
