# WebFilter installer

WiX MSI (x64, per-machine) with a Hub / Agent role choice. Built in M7; design in [MVP-PLAN.md §4.6](../docs/MVP-PLAN.md).

Must remove on uninstall: browser policies (HKLM and every user's HKCU), our WFP filters, the native messaging host registration, and the device's registration with the hub.
