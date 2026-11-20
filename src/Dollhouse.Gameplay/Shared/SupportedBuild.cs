#if DOLLHOUSE_PATCH_BINDINGS
namespace Dollhouse.Core.Patching;
#else
namespace Dollhouse.Gameplay;
#endif

// Standalone original SHA-256 7d96feb44c8426d80d2098ead8f6e2301a278ca914d3ebd340617fe742eccfa4.
// Only valid for that exact sample, not the Steam build. Function/data addresses below are RVAs:
// NativeGame adds the loaded EXE base; memory-patch recipes validate original instructions at these RVAs.
// Evidence and disassembly references:
// docs/investigation/compiled-item-use-demo.md, docs/investigation/repair-gel.md
// and docs/investigation/simultaneous-orgasm.md.
internal static class SupportedBuild
{
    // Item-use hook sites. Patch only these entry points/calls, never the shared mode/count helpers.
    // Req_UseItem(bag, ItemRequest*): original entry checks local progression before handling use.
    public const int RequestUseItem = 0x5359850;
    // Generic script equivalent: (unused context, argument pointer array). Args are bag and request.
    public const int ReflectedUseItem = 0x5357aa0;
    // Existing local validator/consumer: void(bag, ItemRequest*); retained as the request implementation.
    public const int LocalItemUse = 0x54abc40;
    // Two CALL instructions inside LocalItemUse: validation and insufficient-stock diagnostic.
    public const int StockCheckCall = 0x54abd52;
    public const int StockDiagnosticCall = 0x54abd6e;
    // Half-open block after ordinary-item consumption; replaces the unimplemented-effect log.
    // Live values here are bag=r15, request=r14 and count=ebx. Generic completion follows this block.
    public const int SuccessfulOrdinaryItem = 0x54ac0b5;
    public const int SuccessfulOrdinaryItemEnd = 0x54ac0eb;
    // Data byte tested by that log block; validates that the expected branch is being replaced.
    public const int ItemUseLogVerbosity = 0xaff80e8;

    // Shared native APIs. Windows x64 calling convention; all pointers are borrowed unless stated.
    // byte(): 0 means FullUnlock. Read only, used to reproduce handler-local stock behavior.
    public const int ProfileMode = 0x54749c0;
    // Global progression predicate. Used by preservation tests only; never redirected.
    public const int LocalProgressionGate = 0x54772f0;
    // int64(int itemId): the real inventory count. Only the handler's two call sites are redirected.
    public const int InventoryCount = 0x5478010;
    // int(const FString* role, bool anal): packed level*10000 + progress, from
    // the installation/profile-scoped Dilation.txt cache. 0 selects vaginal, 1 anal.
    public const int ReadRoleDilation = 0x5470430;
    // bool(RolesSystemProxy*, const FString* role, bool anal, int delta): calls
    // 0x545ea80, clamps at zero, persists via the native writer, then broadcasts
    // OnRoleDilationChanged at proxy+0xd8. No shared profile-mode patch is needed.
    public const int AddRoleDilation = 0x53f0710;
    // UClass*(): RolesSystemProxy's class, passed to FindProxy.
    public const int RolesClass = 0x53ee190;
    // proxy*(context UObject*, UClass*): resolve the existing role proxy from the bag's context.
    public const int FindProxy = 0x5474de0;
    // void(multicast delegate storage*, parameter record*): existing Unreal delegate dispatcher.
    public const int Broadcast = 0x23ea270;
    // void(bag, role FString*, amount, notify byte, reason FString*). Existing native caps apply.
    public const int AddAffinity = 0x5425020;
    // FString*(destination*, source*): makes an owned string copy for a notification record.
    public const int CopyString = 0x120e670;
    // void(allocation*): releases owned FString character storage using the game's allocator.
    public const int Free = 0x126ccc0;
    // Static FString reason passed to AddAffinity; data, not a callable function.
    public const int UseItemReason = 0x96083c8;
    // void(role FString*, out int experience, out int level): same relationship reader as UI IsCrush.
    // Includes the native FullUnlock policy; do not substitute a separate level calculation.
    public const int RoleFavor = 0x549bb20;

    // Female-threshold binding sites. All three originally supply a constant 100.
    // void(function pointer storage*): accessor registers the native member-call implementation.
    public const int FemaleThresholdAccessor = 0x53ef320;
    // void(unused context, arguments*, float* result): args[0]=proxy, args[1]=role FString*.
    public const int FemaleThresholdGeneric = 0x53efaa0;
    // Half-open exec-wrapper tail: arguments already parsed, frame=rbx, result=rsi, FString=[rsp+20h].
    // The replacement finishes the frame and frees that owned string, then rejoins the original epilogue.
    public const int FemaleThresholdExecTail = 0x53f353b;
    public const int FemaleThresholdExecCleanupEnd = 0x53f3562;
    // Shared float-returning leaf used by other engine code too. Validate its address; NEVER patch it.
    public const int Constant100Function = 0x2392a70;

    // Cumflation: keep wrapper allocation/unwind/cleanup; replace only its gated handler dispatch.
    // Evidence: docs/investigation/cumflation-persistence.md. Requests carry volume DELTAS.
    public const int PropertyDirectDispatch = 0x53b9d7f; // player=edi, owned change array*=rbx.
    public const int PropertyDirectDispatchEnd = 0x53b9d94;
    public const int PropertyDirectCleanup = 0x53b9db0;
    public const int PropertyGenericDispatch = 0x53b8d24; // player=ebx, owned change array=[rsp+40h].
    public const int PropertyGenericDispatchEnd = 0x53b8d3b;
    public const int PropertyGenericCleanup = 0x53b8d57;
    public const int PropertyExecDispatch = 0x53bb2b0; // player=ebx, owned change array=[rbp-20h].
    public const int PropertyExecDispatchEnd = 0x53bb2c6;
    public const int PropertyExecCleanup = 0x53bb2e2;
    // int(): current local player. The original local handler also accepts player 0 as local context.
    public const int LocalPlayerId = 0x547a660;
    // void(int playerId, TArray<FRolePropertyChange>*): original local favor/stamina handler, no Attrs handling.
    public const int LocalRolePropertyChange = 0x54995c0;
    // Fatigue, supported standalone only. FString arguments are borrowed for the
    // synchronous call. Raw reads include native 360-second elapsed recovery.
    public const int ReadStoredStamina = 0x549bdf0; // void(role*, int* current, int* maximum).
    public const int DeductStamina = 0x54a6810; // void(role*, signed cost, const TCHAR* reason).
    public const int StaminaReaderMode = 0x549bd81; // call ProfileMode; cmp al,1; je real read.
    public const int StaminaReaderReal = 0x549bdd0;
    public const int StaminaSummaryMode = 0x549bef4;
    public const int StaminaSummaryReal = 0x549bf35;
    public const int StaminaSaveMode = 0x549cef6; // Only this writer call may observe our scoped authorization.
    public const int ReadIntegerConfig = 0x54349c0; // int(const TCHAR* key, int defaultValue).
    public const int DrinkStaminaCostKey = 0x95f9ac8; // UTF-16 action_drink_stamina_cost, default 3.
    public const int DrinkBookkeeping = 0x54875f0; // void(); keep native progression gating.
    public const int DrinkEffectCost = 0x5428653; // role*=rsi; original block ends at +0x30.
    public const int DrinkEffectCostEnd = 0x5428683;
    public const int DrinkRequestCost = 0x543e894; // role FString lives at rbp-0x71; block ends +0x2d.
    public const int DrinkRequestCostEnd = 0x543e8c1;
    public const int DrinkRequestRoleFromFrame = -0x71; // Borrowed local FString, not a UObject member.
    // const FRoleData*(int roleId). Same reader as GetFemaleByID's native/exec
    // bindings (0x53f15c0 / 0x53f333b); borrowed role-name FString is row+8.
    public const int FemaleById = 0x54724d0;
    public const int FemaleRowRoleName = 8; // Borrowed FString inside FRoleData.
    // Native AInteractive owner role ID (NOT the derived script OwnerAvatar
    // enum). Cached GetIsDrunk reads a DWORD here at 0x79084b, then passes it
    // to GetFemaleByID; PutDownFinished/Sit uses the same field at 0x792a36.
    public const int DrinkOwnerRoleId = 0x3c8;
    // End of SelectMoodPoseUIBase::SetIsLock: native BPL already holds whether
    // any participant has zero stamina. The original CMP tested only IsLock
    // (ownership), after showing the tired overlay. OR BPL into that same byte
    // and preserve ZF for the existing SETNE/return. All call paths share this.
    public const int CardStaminaLock = 0x543c2da;
    // int(role FString*, int attr): role attribute cache; 72 capacity, 73 GI, 74 womb.
    public const int ReadRoleAttribute = 0x549b3a0;
    // First instruction is the five-byte mov [rsp+8],rbx. The capacity leaf replays it
    // before resuming here for all other attributes; no original stack/unwind layout changes.
    public const int ReadRoleAttributeResume = ReadRoleAttribute + 5;
    // Capacity is now selected by native role key through DollhouseSemenCapacity.
    // Keep the previous newer-standalone 1000 mL policy only for unknown roles and
    // historical recipe recognition. This shared entry covers UI, margin and drains.
    public const int SemenCapacityAttribute = 0x48;
    public const int SemenCapacityMl = 1000;
    // void(role FString*, int attr, int value): updates cached attributes and serializes native RoleAttrs.txt.
    public const int WriteRoleAttribute = 0x54a3800;
    // TSet<ERoleAttr,int>::Emplace(map*, out element index*, pair-of-pointers*, optional existing*).
    // Used by the native clear method to construct its attribute notification map.
    public const int AddRoleAttribute = 0x53ef020;
    // Preserve the Unreal exec context in its unused fourth caller home slot before argument parsing.
    // Entry's mov [rsp+8],rbx is replayed by a leaf; RSP/nonvolatile registers stay unchanged.
    public const int PropertyExecEntry = 0x53bb1b0;
    public const int PropertyExecResume = PropertyExecEntry + 5;
    public const int PropertyExecContextHome = 0x20; // entry RSP-relative, becomes [rbp+38h].
    public const int PropertyExecContextFromFrame = 0x38;
    // Clear's final Broadcast(&proxy.OnRoleAttrsChanged, local role/map parameters).
    // Hook after both volume writes; preserve the local broadcast before notifying the room.
    public const int ClearVolumesBroadcast = 0x53f0d46;
    // Native clear: snapshot both attributes at the first zero write; intercept the earlier
    // cleared-event broadcast to preserve UI resets and replace its broken panel-owned alert.
    public const int CaptureDrainWrite = 0x53f0a16;
    public const int DrainClearedBroadcast = 0x53f0b48;
    // UMsgBoxSubsystem direct/exec wrappers own their FString arguments. These two calls
    // enter a borrowed-argument implementation; the adapter leaves wrapper cleanup intact.
    public const int DrainRightDirectCall = 0x53a006f;
    public const int DrainRightExecCall = 0x53a1e21;
    public const int ShowRightMsg = 0x54a6110;
    public const int Allocate = 0x11fda80; // FMemory::Malloc(size_t, uint alignment), paired with Free.
    public const byte DrainRightTip = 40; // MsgBoxEnum::EXPELSEMEN, native avatar sender.
    public const int LocalRoleAttributesDelegate = 0x68;
    public const int RoomRoleAttributesDelegate = 0x78;
    // void(const TCHAR*): existing single-string LogRehostLocal helper; no printf/varargs at this boundary.
    public const int GameplayLog = 0x54a9440;
    // proxy*(world context UObject*, playerId, UClass*): same resolver as
    // GetPlayerRolesSystemProxy and S_RolesInitForNewPlayer. Selects the room's per-player
    // proxy when hosting, otherwise the local proxy. Never publish to a different instance.
    public const int FindPlayerProxy = 0x54a1560;
    // UWorld* GWorld: DATA symbol, not a function. UEngine::GetWorldFromContextObject
    // reads it at RVA 0x45259f9; native world getters also use it at 0x36a5fc2.
    // The action-proxy singleton factory (0x53e8560) instead uses /Engine/Transient as
    // Outer, so neither its GetWorld nor a client-manager lookup can supply this context.
    // Read only on synchronous gameplay callbacks, and never cache across map transitions.
    public const int CurrentWorld = 0xafb9f48;

    // Fully-precompiled startup calls this destructive metadata cleanup after all modules load.
    // It frees module type/method/property lists, namespace maps and native interface registration
    // data (0x533e700..0x533f20b), preventing loaded-method discovery. The native development-
    // mode path already skips this call. Retain metadata only with the direct cumflation visual call;
    // leave cache loading, JIT, transient precompiled-data destruction and hot-reload flags alone.
    public const int DiscardScriptMetadataCall = 0x51537f1;
    public const int DiscardScriptMetadata = 0x533e700; // void(precompiledData*); result is unused.

    // Shipping AngelScript bridge. The bundled fork's vtables differ from current upstream;
    // use these verified concrete implementations, not guessed interface slot numbers.
    public const int ScriptScopeCreate = 0x50b6d40; // scope*(out 16-byte FAngelscriptContextBase*).
    public const int ScriptScopeRelease = 0x519ea00; // void(scope*): pop or return pooled context.
    public const int ScriptGetEngine = 0x11e35c0; // engine*(context*).
    // Direct experiment member dispatch, with the same scoped ABI as native UASFunction.
    public const int GetGameMode = 0x3d605d0; // AGameModeBase*(world context UObject*).
    public const int RoomGameModeClass = 0x538a000; // UClass* AGM_RoomBase::StaticClass().
    public const int GetRoomManager = 0x538c2a0; // bool(gameMode*, int playerId, out ARoomManagerBase*).
    public const int ScriptTypeEngine = 0x17bc7c0; // engine*(type*).
    // Type.GetPlainUserData points to UClass for these UObject script types. The native object-
    // property binder reads this slot at 0x509cb92 and installs it as PropertyClass at +0x78.
    public const int ScriptTypeNativeClass = 0x17bdec0; // borrowed UClass*(type*), type +0x60.
    public const int ScriptTypeSize = 0x1a08c70; // uint(type*), object size at +0x30.
    public const int ScriptTypeId = 0x5145950; // int(type*), native type-ID accessor.
    public const int ScriptTypeById = 0x5146370; // borrowed type*(engine*, int), masks handle flags.
    public const int ScriptSubtypeId = 0x5143ba0; // int(type*, uint index), includes handle qualifiers.
    public const int ScriptSubtype = 0x5143b70; // borrowed type*(type*, uint index), bounded.
    public const int ScriptPropertyCount = 0x455b2f0; // uint(type*), property count at +0x98.
    // int(type*, index, out name*, typeId*, private*, protected*, offset*, reference*,
    //     accessMask*, compositeOffset*, compositeIndirect*). All output pointers are optional.
    // The offset is supplied by live metadata, never hardcoded for HPerformer.
    public const int ScriptProperty = 0x51419e0;
    public const int ScriptMethodByName = 0x513ec80; // borrowed function*(type*, const char* name).
    public const int ScriptFunctionObjectName = 0x5140630; // borrowed const char*(function*).
    public const int ScriptFunctionName = 0x513f130; // borrowed const char*(function*).
    public const int ScriptParameterCount = 0x14bf5b0; // uint(function*), count +0x58.
    // Function vtable +0xc8: int(function*, index, typeId*, modifiers*, name**, default**).
    public const int ScriptParameter = 0x5140850;
    public const int ScriptReturnType = 0x5142120; // int(function*, optional out modifiers*), void=0.
    public const int ScriptSetObject = 0x5197d50; // int(context*, UObject*), native adapter call 0x52e0344.
    // Main-loop frame work: RCX=ticker, XMM1=float delta seconds. Wrapper must call the
    // original first; only this one call site changes, so forwarding cannot recurse.
    public const int FrameTickerCall = 0x4594ba6;
    public const int FrameTickerTick = 0x1222a50;
    // Native FWeakObjectPtr bridge: assign reads a pointer-sized TObjectPtr reference and
    // allocates/copies its object serial (0x15fdb30). Get rejects stale serials and garbage.
    public const int AssignWeakObject = 0x15fdb30; // void(weak8*, UObject**).
    public const int GetWeakObject = 0x15ff3b0; // borrowed UObject*(weak8*), null when invalid.
    // Registry lookups borrow loaded metadata; never create/rebuild a module or compile source.
    public const int ScriptModuleEngine = 0x2e762a0; // engine*(module*).
    // Module vtable 0x94d7168 +0x48/+0x50/+0x70. Namespaces are engine-owned;
    // changing the module's default does not invalidate the borrowed old name.
    // GetFunctionByName reads that namespace and searches loaded globals directly,
    // without the declaration parser (which returned null in the user's test).
    public const int ScriptModuleSetNamespace = 0x51968d0; // int(module*, const char*).
    public const int ScriptModuleGetNamespace = 0x513b7d0; // borrowed const char*(module*).
    public const int ScriptModuleFunctionByName = 0x513d8b0; // borrowed function*(module*, const char*).
    public const int ScriptModuleFunctionCount = 0x33961e0; // uint(module*), +0x140.
    public const int ScriptModuleFunction = 0x513d890; // borrowed function*(module*, uint index).
    public const int ScriptFunctionNamespace = 0x513ff20; // borrowed const char*(function*).
    // Context vtable +0x98/+0x68/+0x90; argument offsets come from Prepare's own signature.
    public const int ScriptSetArgAddress = 0x5196000;
    public const int ScriptSetArgByte = 0x5196030;
    public const int ScriptSetArgDouble = 0x5196090;
    // UWidgetBlueprintLibrary::GetAllWidgetsOfClass: world*, owned TArray<UUserWidget*>*,
    // UClass** (native TSubclassOf reference), bool topLevelOnly. Fresh event-scoped
    // results are restricted by world/class; free the returned array with game Free.
    public const int AllWidgetsOfClass = 0x3709470;
    // UWidget::IsVisible native implementation registered by 0x3671370, also
    // in exec 0x36e1910: obtains live Slate widget, tests visibility mask 0x19,
    // releases the shared reference, returns bool. Never inspect Slate offsets here.
    public const int WidgetVisible = 0x36aa690;
    public const int ScriptModuleFindType = 0x5146400; // borrowed type*(module*, const char* name).
    public const int ScriptTypeName = 0x513f140; // borrowed const char*(type*).
    public const int ScriptGetModule = 0x513eea0; // module*(engine*, const char*, ONLY_IF_EXISTS=0).
    public const int ScriptPrepare = 0x517ad70; // int(context*, function*).
    public const int ScriptExecute = 0x5128f50; // int(context*); FINISHED=0.
    // Context vtable +0xf0. Borrowed return object, valid until next Prepare/release.
    public const int ScriptReturnObject = 0x5141ff0;
    // USkeletalMeshComponent::GetAnimInstance: mov rax,[rcx+0x918]; ret.
    // Matches execGetAnimInstance at 0x3bcc800; receiver comes from GetMainBodyPart.
    public const int MeshAnimInstance = 0x3ba74c0;
    // Exception diagnostics, called only for Execute's EXCEPTION=3. Strings stay context-owned.
    public const int ScriptExceptionString = 0x513c8a0; // const char*(context*).
    public const int ScriptExceptionLine = 0x513c830; // int(context*, out column*, out section name*).
    // UObject* FAngelscriptManager::CurrentWorldContext, DATA. UASFunction adapters save/set/restore
    // this around script calls. Distinct from UWorld* GWorld, and never retained across callbacks.
    public const int ScriptWorldContext = 0xaff0298;

    // Fixed-build object offsets (not RVAs). Precompiled metadata cleanup at
    // 0x533e726 reads its engine pointer from +0x388. The startup hook borrows it
    // before the temporary precompiled-data object is destroyed at 0x5153805.
    public const int PrecompiledEngine = 0x388;
    // asCScriptFunction native VM / parameter / raw entries, populated separately
    // at 0x533f7f4..0x533f80d. Only VM-entry replacement is supported here; reject
    // nonzero parameter/raw entries that could bypass it. Dispatch at 0x50e44b0
    // and top-level Execute at 0x51291d9 call +0x130 with (execution*, args*, result*).
    public const int ScriptVmEntry = 0x130;
    public const int ScriptParameterEntry = 0x138;
    public const int ScriptRawEntry = 0x140;
    // Dispatcher 0x50e4555 advances by this many DWORDs after a VM entry returns.
    // Two global reference arguments use 4 DWORDs: string* then output array*.
    public const int ScriptArgumentWords = 0x160;
    // Precompiled loader 0x533f3ed sets SCRIPT=1; Execute tests it at 0x51291ca.
    public const int ScriptFunctionKind = 0x16c;

    // asBC_CALL and interface-call dispatchers: void(context*, scriptFunction*).
    // Both calls target 0x50e44a0; the caller has already advanced its program counter.
    // Its native fast return at 0x50e4549..0x50e4560 writes the 64-bit value register
    // and pops ScriptArgumentWords * 4 bytes. The eligibility helper has one value int,
    // no receiver, handles or return-object cleanup. Keep other contexts/functions native.
    public const int ScriptDispatch = 0x50e44a0;
    public const int ScriptCallDispatch = 0x512963f;
    public const int ScriptInterfaceDispatch = 0x50e35e0;
    public const int ScriptContextState = 0x20; // ACTIVE=6; not the external Execute result code.
    public const int ScriptContextEngine = 0x18;
    public const int ScriptFunctionEngine = 0x10; // Constructor 0x50b94f1: owning engine.
    public const int ScriptFunctionModule = 0x18; // Constructor 0x50b94fc: owning module.
    public const int ScriptFunctionId = 0x168; // Runtime ID, read by Execute at 0x5129172 and script dispatch lookup.
    public const int ScriptStackPointer = 0x178;
    public const int ScriptValueRegister = 0x180;

    // Object-member byte offsets, NOT image RVAs. Only these fields are accessed on engine objects.
    public const int RolesItemUseDelegate = 0x1a8; // Role item-use multicast delegate in RolesSystemProxy.
    public const int BagItemUseUIDelegate = 0x38;  // Item-use UI multicast delegate in BagItemSystemProxy.
    public const int ScriptFrameCode = 0x20;      // FFrame::Code; null for compiled-in parameter stepping.
    public const int ObjectClass = 0x10;
    // UStruct fast ancestor array/index, verified in native GetRoomManager at 0x538c331..0x538c349.
    public const int ClassAncestors = 0x30;
    public const int ClassDepth = 0x38;
}
