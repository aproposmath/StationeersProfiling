# Stationeers Profiling

Mod to generate timings of any* C# game code function and show it along with the `debugthreads gametick` table.

### How to use

- Download latest release from [Releases](https://github.com/aproposmath/StationeersProfiling/releases) and extract the contents into `<GameFolder>/BepInEx/plugins/`.
- Run `debugthreads gametick` in the in-game console to see the timings.
- Click on the `Enable input` checkbox (alt key for mouse cursor) to edit function names to track.

There are 20 slots for "function sets", each one has 4 settings:
  - Enabled: if false, this set is ignored
  - Names: comma separated list of function names like "Assets.Scripts.Objects.Electrical.ProgrammableChip.Execute", Namespaces are optional.
  - Prefix: prepends this to all the given names above
  - Suffix: appends this to all the given names above

### Example

Track some IC10 operations in all running chips, like `ProgrammableChip._ADD_Operation.Execute`

```
  Enabled = true
  Names = ADD,MUL,SUB,DIV,SB,LB,SBN,LBN
  Prefix = ProgrammableChip._
  Suffix = _Operation.Execute
```

### Limitations

- Currently only functions within Assembly-CSharp.dll (i.e. the core game, no mods) can be timed.
- No timings for async functions (the number calls should still work)
