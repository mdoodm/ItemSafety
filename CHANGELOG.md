# Changelog

For **Schedule I 0.4.7** (Il2Cpp build), MelonLoader v0.7.3 or later. Only the host needs the mod.

## Unreleased

Still alpha. Back up your saves before using it.

### Fixed
- **The cauldron quality fix and cauldron slot log never ran in 0.1.0.** In the game's 0.4.7 build, the methods they hooked are compiled into other methods and never called. They now hook the methods that actually run: `GetState`, `SetItemSlotQuantity_Internal` and `SetStoredInstance_Internal`, and the reader for the cook-finished message. Tested in a solo game: a cook that would have lost its batch was blocked, and slot changes are logged.

### Changed
- At startup the log lists every patched method (`Patched N methods: …`), so you can see that every fix attached.

### Notes
- The dangerous cauldron case is Standard (default-quality) base in the output with better leaves in the slots. The game's own check assumes the new batch is Standard, so it starts the cook, then deletes the batch at the end because it can't stack. When the output holds a better quality than the default, the game already refuses the cook.

## 0.1.0 (alpha)

First test build. Back up your saves before using it.

### Added
- **Safe employee pickup** (`SafeEmployeePickup`). Employees only take what fits in their pockets. The base game took the items from the source first and deleted them when the pockets were full.
- **Safe employee delivery** (`SafeEmployeeDelivery`). When a delivery can't go into the destination or back to the source, the employee keeps it. The base game deleted it ("Item will be lost").
- **Restore carried items on load** (`RestoreEmployeeCarry`). Employees get back what they were carrying when the game saved. The base game never put it back, so it was lost on every reload.
- **Cauldron quality fix** (`CauldronQualityFix`). A cook won't start if its cocaine base can't stack with the output. A batch that finishes anyway is merged at the lower quality instead of being deleted.
- **Cauldron slot log** (`LogCauldronSlots`). Every cauldron slot change on the host is logged with who made it. A joined player dropping petrol by more than one cook uses is flagged `SUSPICIOUS`.
- **Player.log backups** (`BackupPlayerLog`). Keeps the last 15 session logs in `UserData\ItemSafety\Logs`.
