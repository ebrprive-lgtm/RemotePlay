# How to Access the HTTPS Certificate Help Page

## ⭐ When HTTPS is Enabled

When you enable HTTPS on your RemotePlay server, your browser will show a security warning because the certificate is self-signed. **This is normal and expected!** Your connection IS encrypted and secure.

### How to Get to the Certificate Help Page

**The startup logs in RemotePlay will show you instructions:**

When HTTPS is enabled, you'll see in the RemotePlay log window:
```
RemotePlay started
HTTPS is enabled. If you get a certificate warning in your browser:
  1. Click 'Advanced' → 'Proceed anyway' (or continue to site)
  2. Go to Settings → Server → Connection → 'Trust Certificate' button
  3. Follow the step-by-step instructions to install the certificate
  4. Restart your browser and the warning will be gone
```

### Method 1: Through the Browser Warning (Recommended)
1. Open your browser and try to access RemotePlay: `https://your-server-ip:5555`
2. You'll see a browser security warning like "NET:ERR_CERT_AUTHORITY_INVALID"
3. Look for **"Advanced"** or **"More information"** link
4. Click it, then click **"Proceed anyway"** or **"Continue to [URL]"**
5. The RemotePlay app loads - you can now access the Settings page
6. Click **Settings** (gear icon) in the top right
7. Click **Server** in the left sidebar
8. Scroll to the **Connection** section
9. Click the blue **"🔒 Trust Certificate"** button or **"📥 Download .cer"** button
10. A new tab opens with the certificate help page

### Method 2: From Settings Page
1. After clicking "Proceed anyway" in step 4 above:
2. Click **Settings** → **Server** section
3. Find the **Connection** card
4. Click **"🔒 Trust Certificate"** button (opens in new tab)
5. Follow the OS-specific installation steps

### Method 3: Direct URL (if you bypass the warning)
1. After bypassing the certificate warning in your browser
2. Go directly to: `https://your-server-ip:5555/certificate-help`


## What You'll Find on the Certificate Help Page

Once you access `/certificate-help`, you'll find:
- ✅ **Download button** for the certificate file
- ✅ **Step-by-step instructions** for:
  - Windows (MMC certificate store)
  - macOS (Keychain)
  - Linux (system CA certificates)
  - iOS & Android
- ✅ **FAQ section** answering common questions
- ✅ **Security information** about self-signed certificates

## After Installing the Certificate

Once you follow the OS-specific installation steps:
1. Restart your web browser completely
2. Access RemotePlay over HTTPS
3. The security warning should be gone, and you'll see a green lock icon

## Troubleshooting

**Still see the certificate warning?**
- Make sure you installed the certificate in the correct store:
  - Windows: Trusted Root Certification Authorities
  - macOS: System Keychain with "Always Trust"
  - Linux: System ca-certificates
- Restart your browser completely (close all tabs/windows)
- Clear browser cache: Settings → Privacy/History → Clear browsing data
- On mobile: Certificate installation may require special steps

**Can't find the Settings button?**
- It's a gear icon (⚙️) in the top right of the RemotePlay interface
- Make sure JavaScript is enabled in your browser
- Try accessing Settings directly: append `/settings` to your URL

**Can't get past the certificate warning?**
- This is expected! Click "Advanced" → "Proceed anyway"
- Your connection IS encrypted and secure
- The warning only tells you the certificate isn't from a trusted CA

**Need the certificate file directly?**
- Download from: `https://your-server-ip:5555/certificate.cer`
- Or from Settings → Network → **"📥 Download .cer"** button
