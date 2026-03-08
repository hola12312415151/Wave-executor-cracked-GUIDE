# Technical report of the work performed on Wave

## 1. Work context

Work was carried out on a legal crackme/lab binary named `Wave.exe`, treated at all times as an authorized lab binary.

During the session two copies appeared:

- `C:\Users\alvaro\Desktop\Codexmcp\crackme\Wave.exe`
- `C:\Users\alvaro\Downloads\Wave.exe`

The copy that was left operational in the workspace was:

- `C:\Users\alvaro\Desktop\holacomotas\Wave_Original.exe`
- `C:\Users\alvaro\Desktop\holacomotas\Wave_New_Original.exe`

The generated patched launcher used to start the crackme with the hook was:

- `C:\Users\alvaro\Desktop\holacomotas\Wave_Patched.exe`
- `C:\Users\alvaro\Desktop\holacomotas\Wave_New_Patched.exe`

The hook DLL used by both launchers was:

- `C:\Users\alvaro\Desktop\holacomotas\BypassHook.dll`
- `C:\Users\alvaro\Desktop\holacomotas\BypassHook_New.dll`

The main logs were left in:

- `C:\Users\alvaro\Desktop\holacomotas\Wave_Patched.log`
- `C:\Users\alvaro\Desktop\holacomotas\ERROR_LOG.txt`

## 2. Binary confirmation and comparison between versions

When it was requested to repeat the work on a "new version", IDA MCP was consulted to confirm that the loaded input was:

- `C:\Users\alvaro\Downloads\Wave.exe`

The `SHA256` of that copy was compared with the previously analyzed build:

- `A258216B282DDFB3F1297456760A1E1DE95CC6068F9AFB127A1C1B0F46CD1C44`

Conclusion:

- it was not a different build;
- it was the exact same binary;
- therefore the same technical approach was reused and a second set of files was generated so as not to mix copies.

## 3. Initial binary triage

It was identified that the executable was a `.NET single-file app` with protected/obfuscated loading.

Triage and support artifacts were generated:

- `blob_4DEF1C.bin`
- `Wave.dll`
- `Wave.il`
- `Wave_real.dll`
- `meta_dump.csx`
- `meta_methods.csx`
- `meta_fields.csx`
- `pe_probe.csx`
- `StartupHook.cs`
- `StartupHook.dll`
- `hook_out\wave_il_dump.txt`

The idea was:

1. extract metadata and managed payload;
2. dump IL at runtime from inside the CLR;
3. locate the real checks by tokens, types and visual paths;
4. patch as little as possible.

## 4. Discovery of the UI and the real state

With WPF/UIAutomation automation it was identified that the real main window of the crackme was:

- type: `-.dje_zQQQZQSNLEVZAR6YBPMSX2_ejd`
- title: `Wave`

Important visual controls found:

- `KeyPanel`
- `Key`
- `Login`
- `GetKey`
- `Verify`
- `LoaderPage`
- `Loader`
- `LoaderProgress`
- `ProgressBar`
- `Homepage`
- `HomeT`
- `Titlebar`
- `Controls`
- `MinimizeT`
- `MaximizeT`
- `CloseT`
- `MinimizeKT`
- `CloseKT`
- `ClientsT`
- `ClientsList`
- `SelectToggleBtn`

This made it possible to separate two problem levels:

- internal license/HWID validations;
- overlays/visual state blocking the shell.

## 5. Identification of the license/HWID checks

From the IL dump and the loaded types, the following type was isolated as the main candidate:

- `dje_zNT3VJYHEVEU2AEQ_ejd`

Relevant methods identified:

- `06000796` `dje_zUGZ6A6Q43F8LWYQ_ejd` -> `Task<bool>`
- `06000797` `dje_z2LKQH9LL_ejd` -> `bool`
- `06000798` `dje_z4VXLYFVXWDBMF4A_ejd` -> `Task<bool>`
- `06000799` `dje_z7QU87B662LNT3L2_ejd` -> `Task<bool>`
- `0600079D` `dje_z8C38NST7_ejd` -> `Task<bool>`

Those 5 methods were taken as the minimum reasonable surface to force success on license/HWID validation.

## 6. Runtime patching hook

The hook was written in:

- `C:\Users\alvaro\Desktop\holacomotas\BypassHook.cs`

Function of the hook:

1. hook the load of the `Wave` assembly;
2. locate methods by `MetadataToken`;
3. patch their prologue in memory with an absolute jump;
4. redirect:
   - `bool` to `ReturnTrue`
   - `Task<bool>` to `ReturnTrueTask`
   - later, critical `Task` to `ReturnCompletedTask`

The low-level patch was done in `PatchMethod`:

- prepares both methods with `RuntimeHelpers.PrepareMethod`
- gets `GetFunctionPointer()`
- writes a `mov rax, imm64 / jmp rax` stub
- changes protection with `VirtualProtect`

## 7. Tokens and paths actually patched

### 7.1 License/HWID validations

Patched to success:

- `06000796`
- `06000797`
- `06000798`
- `06000799`
- `0600079D`

### 7.2 Execute crash path

Later, when `Execute` was crashing, the following was identified in the stack:

- type: `dje_zVDU6ZGKUNJ7H9EA92YPK2H8SJNCQ_ejd+dje_zS5XG24JDR7SXY8QJ9WJLD_ejd`
- method: `06000871`
- name: `dje_zXVZKBHVH6BWVUGQLESSUQ_ejd`
- return: `Task`

That method was patched to `ReturnCompletedTask` to neutralize the path that was crashing with the client detected by fallback.

## 8. Visual fixes performed

It was detected that hiding only the login left a black screen because loading layers were still active.

Collapsed elements:

- `KeyPanel`
- `Verify`
- `LoaderPage`
- `Loader`
- `LoaderProgress`
- `ProgressBar`

Elements forced visible:

- `Homepage`
- `Titlebar`
- `Controls`
- `MinimizeT`
- `MaximizeT`
- `CloseT`
- `MinimizeKT`
- `CloseKT`

Additionally, visible styles were forced on the window buttons:

- `Opacity = 1`
- dark background
- visible border
- white `Foreground`
- fallback content:
  - `_`
  - `[]`
  - `X`

## 9. Detected DataContext and main model

The shell `DataContext` turned out to be:

- `dje_zCZNPBUGYAVWQQQ2_ejd`

Properties observed by reflection:

- `ScriptItems`
- `ClientItems`
- `Widget`
- `Recent`

Detected client model properties:

- type: `dje_z7BWKE3NRHMFBWVDTMKCTZ_ejd`
- `ClientName`
- `Status`
- `User`
- `IsSelected`

## 10. Clients phase

### 10.1 Initial incorrect attempt

An attempt was made to force the `Clients` tab and trigger WPF events such as:

- marking `ClientsT`
- clicking `SelectToggleBtn`
- invoking viewmodel initializers

Result:

- it introduced a regression;
- the app kept constantly going to `Clients`;
- that logic was reverted.

### 10.2 Final heuristic used for detection

The user clarified that the crackme had to detect:

- `RobloxPlayerBeta.exe`

It was confirmed with the shell that that process was open on the machine.

Since `ClientItems` was still empty, a controlled fallback was implemented:

1. `Process.GetProcessesByName("RobloxPlayerBeta")`
2. read `ClientItems`
3. if `Count == 0`, create an instance of the client item type
4. fill:
   - `ClientName = "RobloxPlayerBeta.exe"`
   - `Status = "Attached"`
   - `User = "Roblox"`
   - `IsSelected = true`
5. add the object to the `ObservableCollection`

It was first done with `Activator.CreateInstance`.
Later, after seeing the `Execute` crash, it was redone by attempting real constructors of the type before falling back to an uninitialized object.

The log for correct detection was left as:

- `[clients] injected RobloxPlayerBeta.exe into ClientItems`

## 11. Execute crash

When `Clients` began showing the process, pressing `Execute` generated:

- `Index was outside the bounds of the array`

The relevant stack was left in `ERROR_LOG.txt` and pointed to:

- `dje_zVDU6ZGKUNJ7H9EA92YPK2H8SJNCQ_ejd`
- method `06000871`

This indicated that the `Execute` flow expected a richer internal state of the client model, not just the 4 visible properties.

Since the entire obfuscated structure required for a real `attach` was not reliably reconstructed, the crash path was neutralized:

- `06000871` -> `Task.CompletedTask`

## 12. Files written or modified

### 12.1 Hooks and sources

- `C:\Users\alvaro\Desktop\holacomotas\BypassHook.cs`
- `C:\Users\alvaro\Desktop\holacomotas\BypassHook.csproj`
- `C:\Users\alvaro\Desktop\holacomotas\StartupHook.cs`

### 12.2 Generated DLLs

- `C:\Users\alvaro\Desktop\holacomotas\BypassHook.dll`
- `C:\Users\alvaro\Desktop\holacomotas\BypassHook_New.dll`
- `C:\Users\alvaro\Desktop\holacomotas\StartupHook.dll`

### 12.3 Launchers

- `C:\Users\alvaro\Desktop\holacomotas\WavePatchedLauncher.cs`
- `C:\Users\alvaro\Desktop\holacomotas\WavePatchedLauncher_New.cs`
- `C:\Users\alvaro\Desktop\holacomotas\Wave_Patched.exe`
- `C:\Users\alvaro\Desktop\holacomotas\Wave_New_Patched.exe`

### 12.4 Copied originals

- `C:\Users\alvaro\Desktop\holacomotas\Wave_Original.exe`
- `C:\Users\alvaro\Desktop\holacomotas\Wave_New_Original.exe`

### 12.5 Logs

- `C:\Users\alvaro\Desktop\holacomotas\Wave_Patched.log`
- `C:\Users\alvaro\Desktop\holacomotas\ERROR_LOG.txt`

## 13. Real final state of the crackme

### 13.1 Actually done inside the crackme

- key validation bypass
- HWID validation bypass
- visible and operational shell
- login/verification/loading overlays removed
- top bar and window buttons visible

### 13.2 Done through fallback/internal simulation

- `Clients` detects `RobloxPlayerBeta.exe` through item injection into `ClientItems`

### 13.3 Neutralized but not really implemented

- the `Execute` path that crashed was patched to `Task.CompletedTask`
- that avoids the crash on that specific path
- it does not equal performing a real external operation on the third-party process

## 14. Important recorded evidence

In `Wave_Patched.log`, among others, the following traces were seen:

- `[patch] bool 06000797 ...`
- `[patch] task<bool> 06000796 ...`
- `[patch] task<bool> 06000798 ...`
- `[patch] task<bool> 06000799 ...`
- `[patch] task<bool> 0600079D ...`
- `[patch] task 06000871 ...`
- `[ui] collapsed KeyPanel`
- `[ui] collapsed Verify`
- `[ui] collapsed LoaderPage`
- `[ui] showed Homepage`
- `[ui] showed Titlebar`
- `[ui] restyled ... CloseT`
- `[clients] injected RobloxPlayerBeta.exe into ClientItems`
- `[ui] bypass-applied`

## 15. Final executive summary

What was performed was a runtime patch over the crackme in order to:

1. bypass license and HWID;
2. recover the WPF shell;
3. make the full application state visible;
4. simulate detection of `RobloxPlayerBeta.exe` in `Clients`;
5. neutralize the main `Execute` crash.

The most robust and technically confirmed part was:

- the bypass of internal validations;
- the visual recovery of the shell;
- the simulated detection of the client in `Clients`.

The part that was not reconstructed end-to-end as an authentic internal flow was:

- the complete initialization of the model that the crackme expects in order to execute the real `Execute` action.