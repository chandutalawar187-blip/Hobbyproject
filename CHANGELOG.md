# Changelog

## [1.2.11] - 2026-09-26

### Fixed

- Refreshed CPU and GPU telemetry acquisition by reading independent sensor groups concurrently and merging them into a single timestamped sample.
- Corrected CPU live-frequency reporting to use Windows' `PercentProcessorPerformance` counter with the processor's maximum clock, allowing boost frequency to be reflected.
- Reported NVIDIA dedicated VRAM from NVAPI's 64-bit framebuffer value instead of the truncated 32-bit WMI `AdapterRAM` field.
- Avoided false RGB keyboard detection when Lenovo firmware reports lighting type 5 without a verified RGB HID interface; supported single-color backlights retain brightness controls.
- Filtered the reported 4800/4800 RPM firmware anomaly at low temperatures and rejected out-of-range fan RPM values.
- Restored smooth telemetry transitions while keeping continuous blink/pulse animations removed.

### Documentation

- Added system architecture, telemetry acquisition, and CPU instruction-pipeline diagrams to the README.

Closes #14.
