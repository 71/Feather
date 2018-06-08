namespace Feather

open System.Linq

open Fody
open Mono.Cecil
open Mono.Cecil.Cil


/// Weaver that will purge all FSharp.Core-related members from the visited module.
type ModuleWeaver() as this =
    inherit BaseModuleWeaver()

    let logDebug str   = Printf.ksprintf this.LogDebug.Invoke str
    let logWarning str = Printf.ksprintf this.LogWarning.Invoke str
    let logError str   = Printf.ksprintf this.LogError.Invoke str

    // ====================================================================================
    // ==== FIELDS AND PROPERTIES =========================================================
    // ====================================================================================

    /// List of all attributes to remove if encountered.
    static let attributesToRemove = [|
        "AutoOpenAttribute";
        "CompilationMappingAttribute";
        "CompilationArgumentsCountsAttribute";
        "SealedAttribute";
        "AbstractClassAttribute";
        "StructAttribute"
    |]

    override __.GetAssembliesForScanning() = Seq.empty
    override __.ShouldCleanReference = true


    // ====================================================================================
    // ==== CLEANING ======================================================================
    // ====================================================================================

    override this.Execute() =
        // 1. Find and remove reference to FSharp.Core
        this.ModuleDefinition.AssemblyReferences
                             .Remove(fun asm -> asm.Name = "FSharp.Core")
        this.ModuleDefinition.ModuleReferences
                             .Remove(fun modl -> modl.Name = "FSharp.Core")
        
        // 2. Remove F# metadata
        this.ModuleDefinition.CustomAttributes
                             .Remove(fun attr -> attr.AttributeType.IsFSharp)
        this.ModuleDefinition.Assembly.CustomAttributes
                                      .Remove(fun attr -> attr.AttributeType.IsFSharp)

        // 3. Purge all C# references from types
        for i, typ in this.ModuleDefinition.Types.GetMutableEnumerator() do
            if this.PurgeType(typ) then
                this.ModuleDefinition.Types.RemoveAt(i)

    member this.PurgeType(ty: TypeDefinition) =
        // 1. Clean attributes
        for i, attr in ty.CustomAttributes.GetMutableEnumerator() do
            if attr.AttributeType.IsFSharp then
                if attributesToRemove.Contains(attr.AttributeType.Name) then
                    ty.CustomAttributes.RemoveAt(i)

        // 2. Clean properties
        for i, prop in ty.Properties.GetMutableEnumerator() do
            if this.PurgeProperty(prop) then
                ty.Properties.RemoveAt(i)
        
        // 3. Clean fields
        for i, field in ty.Fields.GetMutableEnumerator() do
            if this.PurgeField(field) then
                ty.Fields.RemoveAt(i)
        
        // 4. Clean events
        for i, event in ty.Events.GetMutableEnumerator() do
            if this.PurgeEvent(event) then
                ty.Events.RemoveAt(i)
        
        // 5. Clean methods
        for i, method in ty.Methods.GetMutableEnumerator() do
            if this.PurgeMethod(method) then
                ty.Methods.RemoveAt(i)

        // 6. Clean inner types
        for i, typ in ty.NestedTypes.GetMutableEnumerator() do
            if this.PurgeType(typ) then
                ty.NestedTypes.RemoveAt(i)
        
        false
    
    member this.PurgeField(field: FieldDefinition) =
        // 1. Clean attributes
        for i, attr in field.CustomAttributes.GetMutableEnumerator() do
            if attr.AttributeType.IsFSharp then
                field.CustomAttributes.RemoveAt(i)
        
        false

    member this.PurgeEvent(event: EventDefinition) =
        // 1. Clean attributes
        for i, attr in event.CustomAttributes.GetMutableEnumerator() do
            if attr.AttributeType.IsFSharp then
                event.CustomAttributes.RemoveAt(i)
        
        false

    member this.PurgeProperty(prop: PropertyDefinition) =
        // 1. Clean attributes
        for i, attr in prop.CustomAttributes.GetMutableEnumerator() do
            if attr.AttributeType.IsFSharp then
                prop.CustomAttributes.RemoveAt(i)
        
        // 2. Replace getter, setter
        if prop.GetMethod |> (not << isNull) && this.PurgeMethod(prop.GetMethod) then
            prop.GetMethod <- null
        
        if prop.SetMethod |> (not << isNull) && this.PurgeMethod(prop.SetMethod) then
            prop.SetMethod <- null
        
        false


    // ====================================================================================
    // ==== CLEANING METHODS ==============================================================
    // ====================================================================================

    member this.GetReplacementType(typ: TypeReference) =
        match typ.FullName with
        | _ when not typ.IsFSharp -> typ
        | _ -> logWarning "Unknown F# type %s." typ.FullName; typ

    member this.ReplaceParameter(param: ParameterDefinition) =
        if param.ParameterType.IsFSharp then
            param.ParameterType <- this.GetReplacementType(param.ParameterType)

    member this.ReplaceVariable(var: VariableDefinition) =
        if var.VariableType.IsFSharp then
            var.VariableType <- this.GetReplacementType(var.VariableType)

    member this.ReplaceInstruction(instr: Instruction) =
        match instr.Operand with
        | :? TypeReference as typ when typ.IsFSharp ->
            logWarning "Unknown type %s." typ.FullName

        | :? FieldReference as field when field.IsFSharp ->
            logWarning "Unknown field %s." field.FullName
        
        | :? MethodReference as method when method.IsFSharp ->
            logWarning "Unknown method %s." method.FullName
        
        | _ -> ()

    member this.PurgeMethod(method: MethodDefinition) =
        // 1. Clean attributes
        for i, attr in method.CustomAttributes.GetMutableEnumerator() do
            if attr.AttributeType.IsFSharp then
                method.CustomAttributes.RemoveAt(i)

        // 2. Replace parameter and return types
        match this.GetReplacementType(method.ReturnType) with
        | null -> ()
        | typ -> method.ReturnType <- typ

        for param in method.Parameters do
            this.ReplaceParameter(param)

        // 3. Replace variables
        for var in method.Body.Variables do
            this.ReplaceVariable(var)

        // 4. Replace member references and function calls
        for instr in method.Body.Instructions do
            this.ReplaceInstruction(instr)
        
        false