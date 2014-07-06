﻿namespace FSharpVSPowerTools.Refactoring

open Microsoft.VisualStudio.Text
open Microsoft.VisualStudio.Text.Editor
open Microsoft.VisualStudio.Text.Tagging
open Microsoft.VisualStudio.Text.Operations
open Microsoft.VisualStudio.Language.Intellisense
open Microsoft.VisualStudio.Shell.Interop
open System
open FSharpVSPowerTools
open FSharpVSPowerTools.CodeGeneration
open FSharpVSPowerTools.AsyncMaybe
open FSharpVSPowerTools.ProjectSystem

type ResolveUnopenedNamespaceSmartTag(actionSets) =
    inherit SmartTag(SmartTagType.Factoid, actionSets)

type ResolveUnopenedNamespaceSmartTagger
         (view: ITextView, buffer: ITextBuffer, textUndoHistory: ITextUndoHistory,
          vsLanguageService: VSLanguageService, serviceProvider: IServiceProvider,
          projectFactory: ProjectFactory) as self =
    
    let codeGenService: ICodeGenerationService<_, _, _> = upcast CodeGenerationService(vsLanguageService, buffer)
    let tagsChanged = Event<_, _>()
    let mutable currentWord: SnapshotSpan option = None
    let mutable state: (Entity * InsertContext) list option = None 

    let triggerTagsChanged() =
        let span = SnapshotSpan(buffer.CurrentSnapshot, 0, buffer.CurrentSnapshot.Length)
        tagsChanged.Trigger(self, SnapshotSpanEventArgs(span))

    let updateAtCaretPosition() =
        match buffer.GetSnapshotPoint view.Caret.Position, currentWord with
        | Some point, Some word when word.Snapshot = view.TextSnapshot && point.InSpan word -> ()
        | _ ->
            let res =
                maybe {
                    let! point = buffer.GetSnapshotPoint view.Caret.Position
                    let dte = serviceProvider.GetService<EnvDTE.DTE, SDTE>()
                    let! doc = dte.GetActiveDocument()
                    let! project = projectFactory.CreateForDocument buffer doc
                    let! word, _ = vsLanguageService.GetSymbol(point, project) 
                    return point, doc, project, word
                }
            match res with
            | Some (point, doc, project, newWord) ->
                let wordChanged = 
                    match currentWord with
                    | None -> true
                    | Some oldWord -> newWord <> oldWord
                if wordChanged then
                    currentWord <- Some newWord
                    state <- None
                    //let ctx = System.Threading.SynchronizationContext.Current
                    asyncMaybe {
                        let! newWord, sym = vsLanguageService.GetSymbol (point, project) |> liftMaybe
                        // Recheck cursor position to ensure it's still in new word
                        let! point = buffer.GetSnapshotPoint view.Caret.Position |> liftMaybe
                        if not (point.InSpan newWord) then return! liftMaybe None
                        else
                            //do! Async.SwitchToThreadPool() |> liftAsync
                            let! res = 
                                vsLanguageService.GetFSharpSymbolUse(newWord, sym, doc.FullName, project, AllowStaleResults.No) |> liftAsync
                            
                            match res with
                            | Some _ -> return! liftMaybe None
                            | None ->
                                let! checkResults = 
                                    vsLanguageService.ParseFileInProject (doc.FullName, newWord.Snapshot.GetText(), project) |> liftAsync

                                let pos = codeGenService.ExtractFSharpPos point
                                let! parseTree = liftMaybe checkResults.ParseTree
                                
                                let! entityKind = ParsedInput.getEntityKind parseTree pos |> liftMaybe
                                let! entities = vsLanguageService.GetAllEntities (doc.FullName, newWord.Snapshot.GetText(), project)

                                //entities |> Seq.map string |> fun es -> System.IO.File.WriteAllLines (@"l:\entities.txt", es)

                                let isAttribute = entityKind = EntityKind.Attribute
                                let entities =
                                    entities |> List.filter (fun e ->
                                        match entityKind, e.Kind with
                                        | Attribute, Attribute 
                                        | Type, Type
                                        | FunctionOrValue, _ -> true 
                                        | Attribute, _
                                        | Type, _ -> false)

                                let entities = 
                                    entities
                                    |> List.map (fun e -> 
                                         [ yield e.TopRequireQualifiedAccessParent, e.AutoOpenParent, e.Namespace, e.Idents
                                           if isAttribute then
                                               let lastIdent = e.Idents.[e.Idents.Length - 1]
                                               if e.Kind = EntityKind.Attribute && lastIdent.EndsWith "Attribute" then
                                                   yield 
                                                       e.TopRequireQualifiedAccessParent, 
                                                       e.AutoOpenParent,
                                                       e.Namespace,
                                                       Array.append 
                                                          e.Idents.[..e.Idents.Length - 2] 
                                                          [|lastIdent.Substring(0, lastIdent.Length - 9)|] ])
                                    |> List.concat

                                debug "[ResolveUnopenedNamespaceSmartTagger] %d entities found" (List.length entities)
                                let createEntity = ParsedInput.tryFindInsertionContext pos.Line parseTree sym.Text
                                return entities |> List.choose createEntity
                    }
                    |> Async.map (fun result -> 
                         state <- result
                         triggerTagsChanged() )
                    |> Async.StartImmediateSafe
                    
            | _ -> 
                currentWord <- None 
                triggerTagsChanged()

    let docEventListener = new DocumentEventListener ([ViewChange.layoutEvent view; ViewChange.caretEvent view], 
                                                      500us, updateAtCaretPosition)

    let openNamespace (snapshotSpan: SnapshotSpan) (ctx: InsertContext) ns name = 
        use transaction = textUndoHistory.CreateTransaction(Resource.recordGenerationCommandName)
        // first, replace the symbol with (potentially) partially qualified name
        let snapshot = snapshotSpan.Snapshot.TextBuffer.Replace (snapshotSpan.Span, name)
        
        let doc =
            { new IInsertContextDocument<ITextSnapshot> with
                  member x.Insert (snapshot, line, lineStr) = 
                    let pos = snapshot.GetLineFromLineNumber(line).Start.Position
                    snapshot.TextBuffer.Insert (pos, lineStr + Environment.NewLine)
                  member x.GetLineStr (snapshot, line) = snapshot.GetLineFromLineNumber(line).GetText() }
        
        InsertContext.insertOpenDeclaration snapshot doc ctx ns |> ignore
        transaction.Complete()

    let replaceFullyQualifiedSymbol (snapshotSpan: SnapshotSpan) fullSymbolName = 
        use transaction = textUndoHistory.CreateTransaction(Resource.recordGenerationCommandName)
        snapshotSpan.Snapshot.TextBuffer.Replace (snapshotSpan.Span, fullSymbolName) |> ignore
        transaction.Complete()

    let openNamespaceIcon = ResourceProvider.getRefactoringIcon serviceProvider RefactoringIconKind.AddUsing

    let openNamespaceAction snapshot ctx name ns =
        { new ISmartTagAction with
            member x.ActionSets = null
            member x.DisplayText = "open " + ns
            member x.Icon = openNamespaceIcon
            member x.IsEnabled = true
            member x.Invoke() = openNamespace snapshot ctx ns name
        }

    let qualifiedSymbolAction snapshotSpan fullName =
        { new ISmartTagAction with
            member x.ActionSets = null
            member x.DisplayText = fullName
            member x.Icon = null
            member x.IsEnabled = true
            member x.Invoke() = replaceFullyQualifiedSymbol snapshotSpan fullName
        }

    let getSmartTagActions snapshotSpan candidates =
        let openNamespaceActions = 
            candidates
            |> Seq.distinctBy (fun (entity, _) -> entity.Namespace, entity.Name)
            |> Seq.choose (fun (entity, ctx) -> 
                entity.Namespace |> Option.map (openNamespaceAction snapshotSpan ctx entity.Name))
            
        let qualifySymbolActions =
            candidates
            |> Seq.map (fun (entity, _) -> entity.FullRelativeName)
            |> Seq.distinct
            |> Seq.map (qualifiedSymbolAction snapshotSpan)
            
        [ SmartTagActionSet (Seq.toReadOnlyCollection openNamespaceActions)
          SmartTagActionSet (Seq.toReadOnlyCollection qualifySymbolActions) ] 
        |> Seq.toReadOnlyCollection

    interface ITagger<ResolveUnopenedNamespaceSmartTag> with
        member x.GetTags _spans =
            seq {
                match currentWord, state with
                | Some word, Some candidates ->
                    let span = SnapshotSpan(buffer.CurrentSnapshot, word.Span)
                    yield TagSpan<_>(span, ResolveUnopenedNamespaceSmartTag(getSmartTagActions word candidates))
                          :> _
                | _ -> ()
            }
             
        [<CLIEvent>]
        member x.TagsChanged = tagsChanged.Publish

    interface IDisposable with
        member x.Dispose() = 
            (docEventListener :> IDisposable).Dispose()