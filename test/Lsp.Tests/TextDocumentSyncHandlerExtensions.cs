using NSubstitute;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace Lsp.Tests
{
    internal static class TextDocumentSyncHandlerExtensions
    {
        public static ITextDocumentSyncHandler With(DocumentSelector documentSelector, string language) =>
            Substitute.For<ITextDocumentSyncHandler>().With(documentSelector, language);

        public static ITextDocumentSyncHandler With(this ITextDocumentSyncHandler handler, DocumentSelector documentSelector, string language)
        {
            ( (IDidChangeTextDocumentHandler) handler ).GetRegistrationOptions().Returns(new TextDocumentChangeRegistrationOptions { DocumentSelector = documentSelector });
            ( (IDidOpenTextDocumentHandler) handler ).GetRegistrationOptions().Returns(new TextDocumentRegistrationOptions { DocumentSelector = documentSelector });
            ( (IDidCloseTextDocumentHandler) handler ).GetRegistrationOptions().Returns(new TextDocumentRegistrationOptions { DocumentSelector = documentSelector });
            ( (IDidSaveTextDocumentHandler) handler ).GetRegistrationOptions().Returns(new TextDocumentSaveRegistrationOptions { DocumentSelector = documentSelector });
            handler.GetTextDocumentAttributes(Arg.Any<DocumentUri>())
                   .Returns(c => new TextDocumentAttributes(c.Arg<DocumentUri>(), language));

            handler
               .GetTextDocumentAttributes(Arg.Is<DocumentUri>(x => documentSelector.IsMatch(new TextDocumentAttributes(x, language))))
               .Returns(c => new TextDocumentAttributes(c.Arg<DocumentUri>(), language));

            return handler;
        }

        private static void For<T>(this ITextDocumentSyncHandler handler, DocumentSelector documentSelector)
            where T : class, IRegistration<TextDocumentRegistrationOptions>
        {
            var me = handler as T;
            me.GetRegistrationOptions().Returns(GetOptions(me, documentSelector));
        }

        private static TextDocumentRegistrationOptions GetOptions<R>(IRegistration<R> handler, DocumentSelector documentSelector)
            where R : TextDocumentRegistrationOptions, new() =>
            new R { DocumentSelector = documentSelector };
    }
}
