using Dollhouse.Tests;

if (args is ["--no-gameplay-patches-offline"])
{
    GameLauncherTests.Verify();
    NoGameplayPatchesTests.Verify();
    Console.WriteLine("PASS licensing-only launch: parsing, child environment, exact patch allowlist and unchanged default (offline; no game/listener).");
    return 0;
}

if (args is ["--fatigue-offline"])
{
    FatigueFixture.Verify();
    DrinkEligibilityFixture.Verify();
    FatigueRecipeTests.Verify();
    CardFilteringTests.Verify();
    GameplaySettingsTests.Verify();
    ItemUseTests.Verify();
    RuntimeRecipeTests.Verify();
    Console.WriteLine("PASS fatigue costs, gel recovery, settings, native scopes and memory recipe (offline; no game/listener).");
    return 0;
}

if (args is ["--cumflation-restrictions-offline"])
{
    GameplaySettingsTests.Verify();
    RuntimeRecipeTests.Verify();
    Console.WriteLine("PASS cumflation restriction toggle: default OFF, menu/persistence, guarded script dispatch and native ABI (offline; no game/listener).");
    return 0;
}

if (args is ["--card-filtering-offline"])
{
    FatigueRecipeTests.Verify();
    CardFilteringTests.Verify();
    RuntimeRecipeTests.Verify();
    Console.WriteLine("PASS card filtering: ALL-selection matching, fetish/organ values, availability, A/B badges, native styles, prop-only area locks, ownership and memory recipe (offline; no game/listener).");
    return 0;
}

if (args is ["--repair-gel-offline"])
{
    ItemUseTests.Verify();
    RuntimeRecipeTests.Verify();
    Console.WriteLine("PASS Repair Gel: vaginal/anal level and progress, stock, affinity/UI ordering, native ABI/ownership and failure diagnostics (offline; no game/listener).");
    return 0;
}

if (args is ["--startup-workspace-offline"])
{
    StartupWorkspaceTests.Verify();
    Console.WriteLine("PASS startup workspace allocation, ownership, handoff tamper rejection and partition boundaries (offline).");
    return 0;
}

if (args is ["--camera-offline"])
{
    CameraTests.Verify();
    CameraFixture.Verify();
    RuntimeRecipeTests.Verify();
    Console.WriteLine("PASS camera presets, direct script loader/parser ABI, context restoration and in-memory recipe (offline; no game/listener).");
    return 0;
}

if (args is ["--memory-offline"])
{
    await MemoryUsageTests.VerifyAsync();
    StartupTests.Verify();
    MissingStateRecoveryTests.Verify();
    await ServerModuleTests.VerifyAsync();
    await JsonResponseTests.VerifyAsync();
    await ClientReportTests.VerifyAsync();
    await DebugCaptureTests.VerifyExporterAsync();
    Console.WriteLine("PASS bounded startup memory, one-time binary restoration, no-backup startup and request allocations (offline).");
    return 0;
}

if (args is ["--capacity-offline"])
{
    CumflationPatchTests.VerifyCapacity();
    Console.WriteLine("PASS per-character capacities: all ten limits, persistence/fullness/drain and optional own-native export ABI (offline).");
    return 0;
}

if (args is ["--launcher-managed-child", var output, .. var forwarded])
{
    File.WriteAllLines(output, forwarded.Select(argument => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(argument))));
    return 0;
}

if (args is ["--launcher-offline"])
{
    LauncherLoadingTests.Verify();
    Console.WriteLine("PASS suspended launcher loading (own executable/DLL only).");
    return 0;
}

if (args is ["--runtime-offline"])
{
    RuntimeTrustTests.Verify();
    OptionalRuntimeTests.Verify();
    GameLauncherTests.Verify();
    NoGameplayPatchesTests.Verify();
    Console.WriteLine("PASS launcher runtime: own loader fixtures, memory transaction failures, feature recipes, current memory preimages, exports and tamper refusal (no game executed).");
    return 0;
}

if (args is ["--runtime-startup-offline"])
{
    RuntimeTrustTests.Verify();
    DeviceCryptoTests.Verify();
    MissingStateRecoveryTests.Verify();
    Console.WriteLine("PASS unified runtime crypto ABI, preparation, public upgrade and missing-state recovery (offline copies only).");
    return 0;
}

if (args is ["--settings-offline"])
{
    GameplaySettingsTests.Verify();
    Console.WriteLine("PASS gameplay settings: persistence, staged menu edits, native forwarding and in-memory recipe (offline).");
    return 0;
}

if (args is ["--drain-alerts-offline"])
{
    DrainAlertFixture.Verify();
    Console.WriteLine("PASS drain alerts (offline native stubs; no game/listener/requests).");
    return 0;
}

if (args is ["--gameplay-offline"])
{
    FatigueFixture.Verify();
    DrinkEligibilityFixture.Verify();
    FatigueRecipeTests.Verify();
    CardFilteringTests.Verify();
    RuntimeRecipeTests.Verify();
    CumflationPatchTests.Verify();
    ClimaxPatchTests.Verify();
    ItemUseTests.Verify();
    Console.WriteLine("PASS compiled gameplay patches (offline; no game/listener/requests).");
    return 0;
}

if (args is ["--device-crypto-reopen", var library, var fingerprint])
{
    DeviceCryptoTests.Reopen(library, fingerprint);
    return 0;
}

if (args is ["--lifetime-child"])
{
    Console.ReadLine();
    return 0;
}

if (args is ["--offline"])
{
    NoGameplayPatchesTests.Verify();
    FatigueFixture.Verify();
    DrinkEligibilityFixture.Verify();
    FatigueRecipeTests.Verify();
    CardFilteringTests.Verify();
    Console.WriteLine("PASS card filtering: native result availability, A/B badges, existing styles and ownership (offline).");
    CameraTests.Verify();
    CameraFixture.Verify();
    await MemoryUsageTests.VerifyAsync();
    RuntimeTrustTests.Verify();
    OptionalRuntimeTests.Verify();
    GameplaySettingsTests.Verify();
    CumflationPatchTests.Verify();
    Console.WriteLine("PASS cumflation: deltas/capacity, sparse-map ABI, native writer handoff, scope and runtime adapters (offline).");
    ClimaxPatchTests.Verify();
    Console.WriteLine("PASS simultaneous climax: UI relationship policy, native ABI (offline).");
    ItemUseTests.Verify();
    Console.WriteLine("PASS item use: policy/native ABI, scoped stock and current runtime exports (offline).");
    BinaryProgressTests.Verify();
    Console.WriteLine("PASS binary verification progress: elapsed heartbeat and console reporting (offline).");
    DeviceCryptoTests.Verify();
    MissingStateRecoveryTests.Verify();
    Console.WriteLine("PASS missing-state recovery: verified-backup recovery preserves saves; invalid inputs and existing/partial/corrupt state are left intact (offline copies only).");
    Console.WriteLine("PASS device-key compatibility: export/signature ABI, durable identity, failures/retry, concurrency and current runtime preparation (offline).");
    StartupTests.Verify();
    CertificateProviderTests.Verify();
    Console.WriteLine("PASS TLS provider: CryptoAPI, existing PEM/pin preservation, key-container lifetime and offline TLS context (no listener).");
    Console.WriteLine("PASS startup failures: operation/path/error codes, port-denial distinction, certificate write rollback, optional metadata and identity preservation (offline).");
    await TerminalOutputTests.VerifyAsync();
    Console.WriteLine("PASS compact console: aligned handler tags, no normal spacer rows, payload sizes, concurrency, redaction and terminal escaping (offline).");
    GameLauncherTests.Verify();
    await GameSessionLifetimeTests.VerifyAsync();
    Console.WriteLine("PASS game lifetime: child exit stops server; manual stop returns without killing child (own test process only).");
    Console.WriteLine("PASS launcher: CMD argument parity, explicit target, paths with spaces/Unicode, pass-through arguments and exclusive server/clean modes (no game launched).");
    await ShopEndpointTests.VerifyAsync();
    Console.WriteLine("PASS shop: native empty catalog/no-order contracts, sessions, malformed bodies, concurrency, framing and API/echo logging (offline).");
    DeviceIdentityCleanupTests.Verify();
    Console.WriteLine("PASS Windows device-key cleanup: real provider deletion, missing-key retry and unrelated-key preservation (disposable test keys only).");
    GameCleanupTests.Verify();
    Console.WriteLine("PASS exclusive cleanup: original EXE restoration before TLS/license deletion, failure preservation, backups, bounded data, repeat runs and junction rejection (offline; isolated copies only).");
    GameLogConfigurationTests.Verify();
    Console.WriteLine("PASS bundled log configuration: first extraction, reuse, repair, independence from Saved and recreation after cleanup (offline).");
    await DebugCaptureTests.VerifyExporterAsync();
    Console.WriteLine("PASS curl command generation (offline; no requests sent).");
    await DebugCaptureTests.VerifyGroupedNotificationsAsync();
    Console.WriteLine("PASS concurrent debug captures keep each endpoint/ID and curl command in one output block (offline).");
    await ServerModuleTests.VerifyAsync();
    Console.WriteLine("PASS server modules: dispatch, logging/capture, binary bodies, fallback, concurrency, ordering, HEAD and framing (offline).");
    await PakKeyTests.VerifyAsync();
    Console.WriteLine("PASS pakkey: response contract, stable key/current time, logging/capture, missing/invalid keys and method matching (offline).");
    await JsonResponseTests.VerifyAsync();
    Console.WriteLine("PASS shared response encoding: native string compatibility, JSON types, UTF-8 length and HEAD (offline).");
    await UpdateEndpointTests.VerifyAsync();
    Console.WriteLine("PASS notice/hint: sample schemas, nested JSON/casing, English Dollhouse notice, user count/current time and routing (offline).");
    await UserPowTests.VerifyAsync();
    Console.WriteLine("PASS user PoW: native optional-work response, routing/logging and no registration success emulation (offline).");
    await LoginEndpointTests.VerifyAsync();
    Console.WriteLine("PASS local login/me: provisioning, password checks, stable sessions/restart, concurrency, invalid input, private storage and logs (offline).");
    await LicenseKeysetTests.VerifyAsync();
    Console.WriteLine("PASS license keyset: Ed25519 signatures, persistent identity, endpoint contract, tamper rejection and private-key isolation (offline).");
    await LicenseChallengeTests.VerifyAsync();
    Console.WriteLine("PASS license challenge: native request/response, duplicate sessions, context binding, expiry, one-use concurrency and bounded storage (offline).");
    await LicenseLeaseTests.VerifyAsync();
    Console.WriteLine("PASS local lease: P-256 device proof, one-use challenge, signed trust chain, native claims and replay/context rejection (offline).");
    await ManifestTests.VerifyAsync();
    Console.WriteLine("PASS manifest: empty 404 response, no-store, handled logging and method matching (offline).");
    await MailEndpointTests.VerifyAsync();
    Console.WriteLine("PASS mail: empty mailbox, stable query-independent response, framing and routing (offline).");
    await ClientReportTests.VerifyAsync();
    Console.WriteLine("PASS client report: empty HTTP 200, ignored diagnostics, logging/capture and method matching (offline).");
    await UserEmailTests.VerifyAsync();
    Console.WriteLine("PASS user email: native state schema, local binding, sessions, validation, legacy migration, account isolation and persistence (offline).");
    await AccountsTests.VerifyAsync();
    Console.WriteLine("PASS profile accounts: native collection/detail shapes, private snapshots, idempotency, conflicts, sessions, isolation, restart and routing (offline).");
    await ProfileSessionTests.VerifyAsync();
    Console.WriteLine("PASS profile sessions: native heartbeat no-kick regression, claim/save/release, epochs, revisions, restart, retries, takeover, history and failed-write preservation (offline).");
    return 0;
}

return await IntegrationSuite.RunAsync();
