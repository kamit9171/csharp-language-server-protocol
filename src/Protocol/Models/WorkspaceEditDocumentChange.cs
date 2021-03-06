using Newtonsoft.Json;
using OmniSharp.Extensions.LanguageServer.Protocol.Serialization.Converters;

namespace OmniSharp.Extensions.LanguageServer.Protocol.Models
{
    [JsonConverter(typeof(WorkspaceEditDocumentChangeConverter))]
    public struct WorkspaceEditDocumentChange
    {
        public WorkspaceEditDocumentChange(TextDocumentEdit textDocumentEdit)
        {
            TextDocumentEdit = textDocumentEdit;
            CreateFile = null;
            RenameFile = null;
            DeleteFile = null;
        }

        public WorkspaceEditDocumentChange(CreateFile createFile)
        {
            TextDocumentEdit = null;
            CreateFile = createFile;
            RenameFile = null;
            DeleteFile = null;
        }

        public WorkspaceEditDocumentChange(RenameFile renameFile)
        {
            TextDocumentEdit = null;
            CreateFile = null;
            RenameFile = renameFile;
            DeleteFile = null;
        }

        public WorkspaceEditDocumentChange(DeleteFile deleteFile)
        {
            TextDocumentEdit = null;
            CreateFile = null;
            RenameFile = null;
            DeleteFile = deleteFile;
        }

        public bool IsTextDocumentEdit => TextDocumentEdit != null;
        public TextDocumentEdit TextDocumentEdit { get; }

        public bool IsCreateFile => CreateFile != null;
        public CreateFile CreateFile { get; }

        public bool IsRenameFile => RenameFile != null;
        public RenameFile RenameFile { get; }

        public bool IsDeleteFile => DeleteFile != null;
        public DeleteFile DeleteFile { get; }

        public static implicit operator WorkspaceEditDocumentChange(TextDocumentEdit textDocumentEdit) => new WorkspaceEditDocumentChange(textDocumentEdit);

        public static implicit operator WorkspaceEditDocumentChange(CreateFile createFile) => new WorkspaceEditDocumentChange(createFile);

        public static implicit operator WorkspaceEditDocumentChange(RenameFile renameFile) => new WorkspaceEditDocumentChange(renameFile);

        public static implicit operator WorkspaceEditDocumentChange(DeleteFile deleteFile) => new WorkspaceEditDocumentChange(deleteFile);
    }
}
