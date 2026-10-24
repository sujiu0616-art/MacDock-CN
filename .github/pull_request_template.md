## What and why

<!-- What this changes, and the problem it solves. Link the issue: "Fixes #123". -->

## How it was checked

- [ ] `dotnet test MacDock.slnx` passes
- [ ] A bug fix comes with a test that fails without it (or: why it cannot be tested without a window)
- [ ] `MacDock.exe --selftest` passes
- [ ] Tried by hand: <!-- what you did on the desktop, monitors / scaling used -->
- [ ] The taskbar comes back after quitting, and after `Stop-Process -Name MacDock`
- [ ] New interface text goes through `Loc.T` and has its Italian line
