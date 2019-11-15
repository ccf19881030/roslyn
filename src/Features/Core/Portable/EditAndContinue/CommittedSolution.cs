﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

#nullable enable

using System;
using System.Linq;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.DiaSymReader;
using Microsoft.CodeAnalysis.Debugging;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.ErrorReporting;
using Microsoft.CodeAnalysis.Text;
using Roslyn.Utilities;
using System.IO;
using System.Diagnostics;

namespace Microsoft.CodeAnalysis.EditAndContinue
{
    /// <summary>
    /// Encapsulates access to the last committed solution.
    /// We don't want to expose the solution directly since access to documents must be gated by out-of-sync checks.
    /// </summary>
    internal sealed class CommittedSolution
    {
        private readonly DebuggingSession _debuggingSession;

        private Solution _solution;

        internal enum DocumentState
        {
            None = 0,

            /// <summary>
            /// The document belongs to a project whose compiled module has not been loaded yet.
            /// This document state may change to to <see cref="OutOfSync"/>, <see cref="MatchesDebuggee"/> 
            /// or <see cref="DesignTimeOnly"/> once the module has been loaded.
            /// </summary>
            ModuleNotLoaded = 1,

            /// <summary>
            /// The current document content does not match the content the module was compiled with.
            /// This document state may change to <see cref="MatchesDebuggee"/> or <see cref="DesignTimeOnly"/>.
            /// </summary>
            OutOfSync = 2,

            /// <summary>
            /// The current document content matches the content the module was compiled with.
            /// This is a final state. Once a document is in this state it won't switch to a different one.
            /// </summary>
            MatchesDebuggee = 3,

            /// <summary>
            /// The document is not compiled into the module. It's only included in the project
            /// to support design-time features such as completion, etc.
            /// This is a final state. Once a document is in this state it won't switch to a different one.
            /// </summary>
            DesignTimeOnly = 4,
        }

        /// <summary>
        /// Implements workaround for https://github.com/dotnet/project-system/issues/5457.
        /// 
        /// When debugging is started we capture the current solution snapshot.
        /// The documents in this snapshot might not match exactly to those that the compiler used to build the module 
        /// that's currently loaded into the debuggee. This is because there is no reliable synchronization between
        /// the (design-time) build and Roslyn workspace. Although Roslyn uses file-watchers to watch for changes in 
        /// the files on disk, the file-changed events raised by the build might arrive to Roslyn after the debugger
        /// has attached to the debuggee and EnC service captured the solution.
        /// 
        /// Ideally, the Project System would notify Roslyn at the end of each build what the content of the source
        /// files generated by various targets is. Roslyn would then apply these changes to the workspace and 
        /// the EnC service would capture a solution snapshot that includes these changes.
        /// 
        /// Since this notification is currently not available we check the current content of source files against
        /// the corresponding checksums stored in the PDB. Documents for which we have not observed source file content 
        /// that maches the PDB checksum are considered <see cref="DocumentState.OutOfSync"/>. 
        /// 
        /// Some documents in the workspace are added for design-time-only purposes and are not part of the compilation
        /// from which the assembly is built. These documents won't have a record in the PDB and will be tracked as 
        /// <see cref="DocumentState.DesignTimeOnly"/>.
        /// 
        /// A document state can only change from <see cref="DocumentState.OutOfSync"/> to <see cref="DocumentState.MatchesDebuggee"/>.
        /// Once a document state is <see cref="DocumentState.MatchesDebuggee"/> or <see cref="DocumentState.DesignTimeOnly"/>
        /// it will never change.
        /// </summary>
        private readonly Dictionary<DocumentId, DocumentState> _documentState;

        private readonly object _guard = new object();

        public CommittedSolution(DebuggingSession debuggingSession, Solution solution)
        {
            _solution = solution;
            _debuggingSession = debuggingSession;
            _documentState = new Dictionary<DocumentId, DocumentState>();
        }

        // test only
        internal void Test_SetDocumentState(DocumentId documentId, DocumentState state)
        {
            lock (_guard)
            {
                _documentState[documentId] = state;
            }
        }

        public bool HasNoChanges(Solution solution)
            => _solution == solution;

        public Project? GetProject(ProjectId id)
            => _solution.GetProject(id);

        public ImmutableArray<DocumentId> GetDocumentIdsWithFilePath(string path)
            => _solution.GetDocumentIdsWithFilePath(path);

        public bool ContainsDocument(DocumentId documentId)
            => _solution.ContainsDocument(documentId);

        /// <summary>
        /// Captures the content of a file that is about to be overwritten by saving an open document,
        /// if the document is currently out-of-sync and the content of the file matches the PDB.
        /// If we didn't capture the content before the save we might never be able to find a document
        /// snapshot that matches the PDB.
        /// </summary>
        public Task OnSourceFileUpdatedAsync(DocumentId documentId, CancellationToken cancellationToken)
            => GetDocumentAndStateAsync(documentId, cancellationToken, reloadOutOfSyncDocument: true);

        /// <summary>
        /// Returns a document snapshot for given <see cref="DocumentId"/> whose content exactly matches
        /// the source file used to compile the binary currently loaded in the debuggee. Returns null
        /// if it fails to find a document snapshot whose content hash maches the one recorded in the PDB.
        /// 
        /// The result is cached and the next lookup uses the cached value, including failures unless <paramref name="reloadOutOfSyncDocument"/> is true.
        /// </summary>
        public async Task<(Document? Document, DocumentState State)> GetDocumentAndStateAsync(DocumentId documentId, CancellationToken cancellationToken, bool reloadOutOfSyncDocument = false)
        {
            Document? document;

            lock (_guard)
            {
                document = _solution.GetDocument(documentId);
                if (document == null)
                {
                    return (null, DocumentState.None);
                }

                if (document.FilePath == null)
                {
                    return (null, DocumentState.DesignTimeOnly);
                }

                if (_documentState.TryGetValue(documentId, out var documentState))
                {
                    switch (documentState)
                    {
                        case DocumentState.MatchesDebuggee:
                            return (document, documentState);

                        case DocumentState.DesignTimeOnly:
                            return (null, documentState);

                        case DocumentState.ModuleNotLoaded:
                            // module might have been loaded since the last time we checked
                            break;

                        case DocumentState.OutOfSync:
                            if (reloadOutOfSyncDocument)
                            {
                                break;
                            }

                            return (null, documentState);

                        case DocumentState.None:
                            throw ExceptionUtilities.Unreachable;
                    }
                }
            }

            var (matchingSourceText, isLoaded, isMissing) = await TryGetPdbMatchingSourceTextAsync(document.FilePath, document.Project.Id, cancellationToken).ConfigureAwait(false);

            lock (_guard)
            {
                // only OutOfSync and ModuleNotLoaded states can be changed:
                if (_documentState.TryGetValue(documentId, out var documentState) &&
                    documentState != DocumentState.OutOfSync &&
                    documentState != DocumentState.ModuleNotLoaded)
                {
                    return (document, documentState);
                }

                DocumentState newState;
                Document? matchingDocument;

                if (isMissing)
                {
                    if (isLoaded)
                    {
                        // Source file is not listed in the PDB. This may happen for a couple of reasons:
                        // The library wasn't built with that source file - the file has been added before debugging session started but after build captured it.
                        // This is the case for WPF .g.i.cs files.
                        matchingDocument = null;
                        newState = DocumentState.DesignTimeOnly;
                    }
                    else
                    {
                        // The module the document is compiled into has not been loaded yet.
                        matchingDocument = document;
                        newState = DocumentState.ModuleNotLoaded;
                    }
                }
                else if (matchingSourceText != null)
                {
                    if (document.TryGetText(out var sourceText) && sourceText.ContentEquals(matchingSourceText))
                    {
                        matchingDocument = document;
                    }
                    else
                    {
                        _solution = _solution.WithDocumentText(documentId, matchingSourceText, PreservationMode.PreserveValue);
                        matchingDocument = _solution.GetDocument(documentId);
                    }

                    newState = DocumentState.MatchesDebuggee;
                }
                else
                {
                    matchingDocument = null;
                    newState = DocumentState.OutOfSync;
                }

                _documentState[documentId] = newState;
                return (matchingDocument, newState);
            }
        }

        public void CommitSolution(Solution solution, ImmutableArray<Document> updatedDocuments)
        {
            lock (_guard)
            {
                // Changes in the updated documents has just been applied to the debuggee process.
                // Therefore, these documents now match exactly the state of the debuggee.
                foreach (var document in updatedDocuments)
                {
                    // Changes in design-time-only documents should have been ignored.
                    Debug.Assert(_documentState[document.Id] != DocumentState.DesignTimeOnly);

                    _documentState[document.Id] = DocumentState.MatchesDebuggee;
                    Debug.Assert(document.Project.Solution == solution);
                }

                _solution = solution;
            }
        }

        private async Task<(SourceText? Source, bool IsLoaded, bool IsMissing)> TryGetPdbMatchingSourceTextAsync(string sourceFilePath, ProjectId projectId, CancellationToken cancellationToken)
        {
            var (symChecksum, algorithm, isLoaded) = await TryReadSourceFileChecksumFromPdb(sourceFilePath, projectId, cancellationToken).ConfigureAwait(false);
            if (symChecksum.IsDefault)
            {
                return (Source: null, isLoaded, IsMissing: true);
            }

            if (!PathUtilities.IsAbsolute(sourceFilePath))
            {
                EditAndContinueWorkspaceService.Log.Write("Error calculating checksum for source file '{0}': path not absolute", sourceFilePath);
                return (Source: null, isLoaded, IsMissing: false);
            }

            try
            {
                using var fileStream = new FileStream(sourceFilePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                var sourceText = SourceText.From(fileStream, checksumAlgorithm: algorithm);
                return (sourceText.GetChecksum().SequenceEqual(symChecksum) ? sourceText : null, isLoaded, IsMissing: false);
            }
            catch (Exception e)
            {
                EditAndContinueWorkspaceService.Log.Write("Error calculating checksum for source file '{0}': '{1}'", sourceFilePath, e.Message);
                return (Source: null, isLoaded, IsMissing: false);
            }
        }

        private async Task<(ImmutableArray<byte> Checksum, SourceHashAlgorithm Algorithm, bool IsLoaded)> TryReadSourceFileChecksumFromPdb(string sourceFilePath, ProjectId projectId, CancellationToken cancellationToken)
        {
            try
            {
                var (mvid, mvidError) = await _debuggingSession.GetProjectModuleIdAsync(projectId, cancellationToken).ConfigureAwait(false);
                if (mvid == Guid.Empty)
                {
                    EditAndContinueWorkspaceService.Log.Write("Source '{0}' doesn't match PDB: can't read MVID ('{1}')", sourceFilePath, mvidError);
                    return default;
                }

                cancellationToken.ThrowIfCancellationRequested();

                // Dispatch to a background thread - reading symbols requires MTA thread.
                if (Thread.CurrentThread.GetApartmentState() != ApartmentState.MTA)
                {
                    return await Task.Factory.StartNew(ReadChecksum, cancellationToken, TaskCreationOptions.None, TaskScheduler.Default).ConfigureAwait(false);
                }
                else
                {
                    return ReadChecksum();
                }

                (ImmutableArray<byte> Checksum, SourceHashAlgorithm Algorithm, bool IsLoaded) ReadChecksum()
                {
                    DebuggeeModuleInfo? moduleInfo;
                    bool isLoaded = false;
                    try
                    {
                        moduleInfo = _debuggingSession.DebugeeModuleMetadataProvider.TryGetBaselineModuleInfo(mvid);
                    }
                    catch (Exception e) when (FatalError.ReportWithoutCrashUnlessCanceled(e))
                    {
                        EditAndContinueWorkspaceService.Log.Write("Source '{0}' doesn't match PDB: unexpected exception: {1}", sourceFilePath, e.Message);
                        return (default, default, isLoaded);
                    }

                    if (moduleInfo == null)
                    {
                        EditAndContinueWorkspaceService.Log.Write("Source '{0}' doesn't match PDB: module not loaded", sourceFilePath);
                        return (default, default, isLoaded);
                    }

                    isLoaded = true;

                    try
                    {
                        var symDocument = moduleInfo.SymReader.GetDocument(sourceFilePath);
                        if (symDocument == null)
                        {
                            EditAndContinueWorkspaceService.Log.Write("Source '{0}' doesn't match PDB: no SymDocument", sourceFilePath);
                            return (default, default, isLoaded);
                        }

                        var symAlgorithm = SourceHashAlgorithms.GetSourceHashAlgorithm(symDocument.GetHashAlgorithm());
                        if (symAlgorithm == SourceHashAlgorithm.None)
                        {
                            // unknown algorithm:
                            EditAndContinueWorkspaceService.Log.Write("Source '{0}' doesn't match PDB: unknown checksum alg", sourceFilePath);
                            return (default, default, isLoaded);
                        }

                        var symChecksum = symDocument.GetChecksum().ToImmutableArray();

                        return (symChecksum, symAlgorithm, isLoaded);
                    }
                    catch (Exception e)
                    {
                        EditAndContinueWorkspaceService.Log.Write("Source '{0}' doesn't match PDB: error reading symbols: {1}", sourceFilePath, e.Message);
                        return (default, default, isLoaded);
                    }
                }
            }
            catch (Exception e) when (FatalError.ReportWithoutCrashUnlessCanceled(e))
            {
                EditAndContinueWorkspaceService.Log.Write("Source '{0}' doesn't match PDB: unexpected exception: {1}", sourceFilePath, e.Message);
                return default;
            }
        }
    }
}
