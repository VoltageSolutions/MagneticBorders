# Start at login

Windows starts every program listed under a certain registry key when the user signs in:

```text
HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run
```

Each value is a name and a command line. To turn on **Start at login**, Magnetic Borders writes a value named `MagneticBorders` that holds the quoted path of its own exe. To turn it off, it deletes the value.

Because the key is under the current user, no administrator rights are needed.

The running Magnetic Borders does this, not the settings app, so the path always points at the copy that is actually running. The change takes effect at once. See the [settings contract](../spec/contract.md).

=== "C++"

    ```cpp
    void set_start_at_login(bool enabled, const std::filesystem::path& exe) {
        if (enabled) {
            const std::wstring value = L"\"" + exe.wstring() + L"\"";
            RegSetKeyValueW(HKEY_CURRENT_USER, kRunKey, kValue, REG_SZ,
                            value.c_str(), (value.size() + 1) * sizeof(wchar_t));
        } else {
            RegDeleteKeyValueW(HKEY_CURRENT_USER, kRunKey, kValue);
        }
    }
    ```

=== "Rust"

    ```rust
    // misc.rs: Some(exe) writes the value, None deletes it
    pub fn set_run_at_login(exe: Option<&Path>) -> Result<(), String> { /* ... */ }
    ```

=== "C#"

    ```csharp
    using var k = Registry.CurrentUser.CreateSubKey(Key);
    if (on) k.SetValue(Name, Environment.ProcessPath);
    else    k.DeleteValue(Name, false);
    ```

## Why not a service

A Windows service runs in a separate session and cannot see or hook the user's desktop. A Run entry gives the same always-on behavior and runs in the user's session.
