## What

## Why

## Safety checklist
- [ ] No raw sector writes; every disk change goes through `WmiStorageOperations`
- [ ] New write paths are wrapped in `Guarded(...)` (sleep inhibitor + operation log)
- [ ] Planner validation + UI confirmation exist for any new operation
- [ ] Tested on a VHDX, not on a real disk
