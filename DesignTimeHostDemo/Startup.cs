using System.IO;
using System.Linq;
using System.Text;
using DesignTimeHostDemo.Omnisharp;
using Microsoft.CodeAnalysis.Recommendations;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Owin;
using Newtonsoft.Json;
using Owin;

namespace DesignTimeHostDemo
{
    public class Startup
    {
        private readonly MyCustomWorkspace _workspace;

        public Startup(MyCustomWorkspace workspace)
        {
            _workspace = workspace;
        }

        public void Configuration(IAppBuilder app)
        {
            app.Map("/codecheck", map =>
            {
                map.Run(async context =>
                {
                    Request request = EnsureBufferUpdated(context);

                    var quickFixes = new
                    {
                        QuickFixes = new object[] { }
                    };

                    await context.Response.WriteAsync(JsonConvert.SerializeObject(quickFixes));
                });
            });

            app.Map("/autocomplete", map =>
            {
                map.Run(async context =>
                {
                    var completions = Enumerable.Empty<AutoCompleteResponse>();

                    Request request = EnsureBufferUpdated(context);

                    var documentIds = _workspace.CurrentSolution.GetDocumentIdsWithFilePath(request.FileName);

                    var documentId = documentIds.FirstOrDefault();
                    if (documentId != null)
                    {
                        var document = _workspace.CurrentSolution.GetDocument(documentId);
                        var sourceText = await document.GetTextAsync();
                        var position = sourceText.Lines.GetPosition(new LinePosition(request.Line - 1, request.Column));
                        var model = await document.GetSemanticModelAsync();
                        var symbols = Recommender.GetRecommendedSymbolsAtPosition(model, position, _workspace);

                        completions = symbols.Select(s => new AutoCompleteResponse { CompletionText = s.Name, DisplayText = s.Name });
                    }

                    await context.Response.WriteAsync(JsonConvert.SerializeObject(completions));
                });
            });
        }

        private Request EnsureBufferUpdated(IOwinContext context)
        {
            var inputReader = new JsonTextReader(new StreamReader(context.Request.Body));
            var serializer = new JsonSerializer();
            var request = serializer.Deserialize<Request>(inputReader);

            foreach (var documentId in _workspace.CurrentSolution.GetDocumentIdsWithFilePath(request.FileName))
            {
                var buffer = Encoding.UTF8.GetBytes(request.Buffer);
                var sourceText = SourceText.From(new MemoryStream(buffer), encoding: Encoding.UTF8);
                _workspace.OnDocumentChanged(documentId, sourceText);
            }

            return request;
        }

    }
}
