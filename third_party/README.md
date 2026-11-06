# Upstream source import

Original GPL source is intentionally not included in the initial foundation archive. Import the exact official TightVNC 2.8.90 source ZIP using `python tools/vendor_upstream.py --zip <local-source-zip>` before beginning the Windows C++ fork. Keep all existing copyright notices and original license files in the imported source.

After importing, review identity collisions with `python tools/audit_identifiers.py --source third_party/tightvnc-2.8.90-gpl`. No automatic rewrite is performed.
