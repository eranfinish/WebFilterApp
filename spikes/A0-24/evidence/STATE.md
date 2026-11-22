# A0-24 progress log (no names or emails in this file)

Device: emulator-5580, Medium_Phone_API_37.0, Android 17 (API 37), build CE2A.260420.051.A2, GMS 26.45.31, Chrome 145.0.7632.218. Emulator started with the command from the brief (port 5580, 3072 MB, -crash-report-mode disabled, -no-metrics).

Done:
- 00-baseline.txt: no owners, one user (0), 0 accounts, device_provisioned=1, private_dns_mode null.
- App built: spikes/A0-24/app (package com.webfilter.spike.a24, compileSdk 36, targetSdk 36, minSdk 31). Build command: java -classpath gradle\wrapper\gradle-wrapper.jar org.gradle.wrapper.GradleWrapperMain --offline :app:assembleDebug, with ANDROID_HOME set in the shell.
- 01-install-and-owner.txt: install -t OK, `dpm set-device-owner` OK on user 0, 0 accounts.
- 02/03: user 10 (fl-kid) created with pm create-user, started, screen_off_timeout=1800000 and stylus_handwriting_enabled=0 set for it, logcat -G 16M, switched to user 10.

Waiting: parent adds the supervised child account on user 10 by hand (message sent to the starting session, local_acf56613, 2026-11-22).

Not yet done: Private DNS (setGlobalSetting), restriction, resolution checks, parent checks, report.

## Step 5 (applied)
- 04-verify-family-link.txt: GMS supervision is Profile Owner of user 10; our DPC is Device Owner on user 0; accounts user 0 = 0, user 10 = 1.
- 05-private-dns-apply.txt: dedicated API refused ("unaffiliated users"); setGlobalSetting private_dns_mode and private_dns_specifier OK; DPM getter PROVIDER_HOSTNAME; dumpsys connectivity validates [1.0.0.3,1.1.1.3].
- 06-u0-checks.txt: DPC resolve malware.testcategory.com -> 0.0.0.0,::; www.ynet.co.il -> 23.221.30.50; host nslookup via 1.1.1.3 -> 0.0.0.0 / ::. Shell ping of malware returned 127.0.0.1 (same as A0.22; not explained).
- 07-u0-restriction-dumpsys.txt: disallow_config_private_dns in global and effective restrictions of user 10 (running, foreground).
- 08-logcat-family-link-check.txt: 99 THIRD_PARTY_DEVICE_MANAGEMENT_REQUIRED lines, last burst 23:09:05 device time; no account removal line.

Pending: parent's u10 checks (ynet, malware, Private DNS lock, Chrome secure DNS, classroom, YouTube). Recheck account count after 23:24 device time (15 minutes after last burst).
