# StarLocator

[![Build APK](https://github.com/theJaraxleNUKE/StarLocator/actions/workflows/build-apk.yml/badge.svg)](https://github.com/theJaraxleNUKE/StarLocator/actions/workflows/build-apk.yml)
[![Latest release](https://img.shields.io/github/v/release/theJaraxleNUKE/StarLocator)](https://github.com/theJaraxleNUKE/StarLocator/releases/latest)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

An Android app that works out **where on Earth you are from a photo of the night sky**. It uses only the phone's camera, its accelerometer and its clock: no GPS, no Wi-Fi, no cell towers, no Internet.

The APK requests **one permission, `CAMERA`**. Every CI build checks that no network or location permission has been added, and fails if one has.

Written in C# for .NET 10 for Android. The star-identification core is a platform-independent .NET library.

> **Status:** the solver is verified against synthetic sky images (see [Accuracy](#accuracy)). The app has **not yet been field-tested on real phones**; real-world accuracy is unmeasured.

---

## Contents

- [Download & install](#download--install)
- [Using the app](#using-the-app)
- [How it works](#how-it-works)
- [Accuracy](#accuracy)
- [Limitations](#limitations)
- [Building from source](#building-from-source)
- [CI and releases](#ci-and-releases)
- [Project layout](#project-layout)
- [Credits](#credits)

---

## Download & install

1. Open [**Releases**](https://github.com/theJaraxleNUKE/StarLocator/releases/latest) on the phone and download `StarLocator-<version>.apk`.
2. Open the file. When Android asks, allow **"Install unknown apps"** for your browser or file manager.
3. Requirements:
   - Android 8.0 (API 26) or newer, on a 64-bit ARM phone (or an x64 emulator).
   - A back camera that supports **manual exposure** (Camera2 `MANUAL_SENSOR`). The app tells you if yours doesn't.

Each release also has a `.sha256` file for checking the download.

## Using the app

1. **Go outside on a clear night.** Avoid the Moon and bright lights. The interface is dim red so it doesn't spoil your night vision.
2. **Brace the phone** so it can't move: on a rock, car roof, wall or tripod. Point the back camera **45–90° up** at clear sky. The live "camera elevation" readout helps you aim.
3. Tap **CAPTURE**. The defaults are a **4 s** exposure at **ISO 1600**; both are clamped to what the camera supports. Don't touch the phone until the result appears.
4. Read the result:
   - latitude/longitude in degrees-minutes-seconds and decimal degrees;
   - an estimated 1-σ error in km;
   - the photo with **identified stars circled in green and named**. Detections it couldn't match are circled in red.
5. **OPEN IN MAP** passes the fix to any installed map app as a `geo:` link. Offline map apps work.
6. **Calibrate once for much better accuracy.** Take **4–6 shots**, tilting the phone differently for each (portrait, landscape, leaning left, leaning right), then tap **CALIBRATE**. This measures your phone's accelerometer bias and how far the camera sits off-square from the accelerometer. It is saved per camera and applied to every later single shot. **FORGET CALIBRATION** clears it.
7. Optional: tick **Save frames for PC replay**. Each frame is then written as `.pgm` + `.txt` to `Android/data/io.github.thejaraxlenuke.starlocator/files/frames/` so you can re-solve it on a PC (see [Building from source](#building-from-source)).

If too few stars are found, increase the exposure or ISO, or find a darker spot.

## How it works

A phone can act like a sailor's sextant:

| Navigation tool | Phone part | What it gives |
|---|---|---|
| Sextant | Camera + star identification | Which way the phone points relative to the stars |
| Artificial horizon | Accelerometer | Which way is "straight up" in the phone's frame |
| Chronometer | Clock | How far the Earth has rotated |

The star tracker comes from **Marbel, Ben-Moshe & Yozevitch, "Star-Tracker Algorithm for Smartphones and Commercial Micro-Drones", *Sensors* 20(4):1106, 2020** ([open access](https://pmc.ncbi.nlm.nih.gov/articles/PMC7070887/)). The paper gives the camera's **orientation** only. StarLocator adds the gravity vector and the clock to turn that into a **position**.

### Pipeline

| # | Step | File |
|---|---|---|
| 1 | **Star detection.** Per-block background and robust noise estimate, threshold, 8-connected components, background-subtracted sub-pixel centroids. Blobs that are too small (hot pixels) or too large (Moon, lamps) are rejected. | `StarDetector.cs` |
| 2 | **Star Pattern Hash Table** (paper §3.1). Every triplet of catalog stars brighter than magnitude 4.0 whose sides are 1–30° is keyed by its three sorted, quantised angular distances. That is 518 stars and 94,439 triangles, built at start-up in about 0.05 s on a desktop CPU. | `StarPatternHash.cs` |
| 3 | **Matching + confidence voting** (paper §3.2). Triangles from the 24 brightest detections are looked up with a ±0.4° tolerance. Each catalog match votes for a star label, weighted by 1/(number of matches), so ambiguous triangles count less. | `StarIdentifier.cs` |
| 4 | **Validation** (paper §3.3). Pairs of voted labels give exact orientation hypotheses (TRIAD). A hypothesis is accepted only if **at least 4 independently voted labels agree** with it and the matched-star count beats chance by more than 5σ. This is what rejects false fixes. | `StarIdentifier.cs` |
| 5 | **Refinement.** Least-squares orientation (Davenport q-method) over all matched stars down to magnitude 5.5. The focal length (±8%) and radial distortion K1 are self-calibrated from the stars, and focal scales 0.90–1.10 are tried if the first attempt fails. | `StarIdentifier.cs`, `Attitude.cs` |
| 6 | **Gravity → zenith.** The accelerometer is averaged over exactly the exposure window to give "up" in the phone frame. Atmospheric refraction is removed per star (Bennett). The solved orientation maps "up" onto the sky. | `PositionSolver.cs`, `Astronomy.cs` |
| 7 | **Zenith → latitude/longitude.** The zenith is precessed from J2000 to the date (IAU 1976) and rotated by Greenwich Mean Sidereal Time. **Latitude = declination of the zenith; longitude = its right ascension − GMST.** | `Astronomy.cs` |
| 8 | **Phone calibration** (multi-shot). Model per shot: `a = (I − [w×])·M·v + b`. A Gauss-Newton fit over 4+ shots solves for the position `v`, the accelerometer bias `b` and the camera-to-accelerometer misalignment `w` (9 unknowns); 3 shots solve for `v` and `b` only. Badly conditioned shot sets are rejected with a message. | `PositionSolver.cs` |

Camera intrinsics come from Android's factory `LENS_INTRINSIC_CALIBRATION` when the phone provides it. Otherwise they come from focal length ÷ sensor size. Lens distortion is corrected by the camera itself where supported, and otherwise from `LENS_DISTORTION`. Exposure uses Camera2 manual mode with focus at infinity, reading the raw luminance (Y) plane.

The star catalog is the **Yale Bright Star Catalog, 5th ed.** (9,096 stars, J2000), embedded in the core library.

## Accuracy

All figures below come from the test suite in `tests/StarLocator.Tests`. It renders 4000×3000 sky images for known places and times, including sensor noise, a light-pollution gradient, cloud, fake planes/planets, ±5% focal-length error, lens distortion and star-position jitter. **They are simulator results, not field measurements.**

**Star matching only (perfect accelerometer), 40 random frames per row:**

| Per-star position noise | Frames solved | Wrong fixes | Median error | 90th percentile |
|---|---|---|---|---|
| 0 px (0.00°) | 37 / 40 | 0 | 1.6 km | 2.9 km |
| 3 px (0.06°) | 39 / 40 | 0 | 2.0 km | 3.9 km |
| 6 px (0.11°) | 38 / 40 | 0 | 3.5 km | 5.8 km |
| 10 px (0.19°) | 36 / 40 | 0 | 5.7 km | 12.2 km |

Pure noise fields with random blobs gave **0 false fixes in 30 trials**.

**With realistic phone errors** (accelerometer bias 0.15 m/s² ≈ 0.9°, camera mounting error 0.4°, principal point off by 20 px):

| Mode | Error |
|---|---|
| Single shots, uncalibrated | 24–55 km |
| 6-shot calibration | 0.8 km |
| Later single shots using that calibration | 0.5–3.9 km |

**Expect worse on a real phone.** The paper measured 0.1–0.3° orientation error on smartphones, which on its own corresponds to roughly 11–33 km. Atmospheric seeing, focus and lens quality add to that. Rules of thumb:

- 1° of tilt error ≈ **111 km**. The accelerometer is usually the biggest error source, so calibrate.
- 1 s of clock error ≈ **0.46 km** of longitude at the equator. Without network time the phone clock can drift.
- The ± figure the app shows is a model-based estimate, not a guarantee.

## Limitations

- Works at night only, and needs about 6+ identifiable stars: clear sky, nothing blocking the view.
- Needs manual exposure (`MANUAL_SENSOR`) on the back camera.
- Effects ignored because they are far below the sensor limits: nutation (≈17″), annual aberration (≈20″), UT1−UTC (<0.9 s), polar motion, star proper motion and deflection of the vertical.
- Planets are not in the catalog. They are treated as outliers and ignored.

## Building from source

Requirements: [.NET 10 SDK](https://dotnet.microsoft.com/download), the Android workload, and JDK 17 (the CI uses Temurin 17).

```bash
dotnet workload install android

# Solver tests: no phone or Android SDK needed
dotnet run --project tests/StarLocator.Tests -c Release            # unit + end-to-end tests
dotnet run --project tests/StarLocator.Tests -c Release -- sweep   # 160-frame robustness sweep
dotnet run --project tests/StarLocator.Tests -c Release -- replay path/to/frame_YYYYMMDD_HHMMSS.pgm

# Android SDK packages the app needs (once), then build / install
dotnet build src/StarLocator.Android -t:InstallAndroidDependencies -f net10.0-android \
  -p:AndroidSdkDirectory=<path-to-android-sdk> -p:AcceptAndroidSDKLicenses=True
dotnet publish src/StarLocator.Android -c Release -f net10.0-android     # -> *-Signed.apk
dotnet build   src/StarLocator.Android -c Release -t:Install              # to a USB-connected phone
```

You can also open `StarLocator.sln` in an IDE that supports .NET 10 Android projects and run it on a device.

## CI and releases

`.github/workflows/build-apk.yml` runs on GitHub's `ubuntu-latest` runner.

| Trigger | What happens |
|---|---|
| Push to `main`, pull request, or manual run | Runs the solver tests, builds the APK, checks its signature, package and permissions, and uploads it as a **workflow artifact** |
| Push a tag `v*` (e.g. `v1.1.0`) | Everything above, **plus a GitHub Release** with `StarLocator-<version>.apk` and its `.sha256` attached |

To publish a new version:

```bash
git tag v1.1.0
git push origin v1.1.0
```

The release's `versionName` comes from the tag. Its `versionCode` is the workflow run number, so it always increases.

### Signing key (recommended)

Android only installs an update over an existing copy if both are signed with the **same key**. Without a key of your own, each CI run signs with a fresh throwaway debug key, and you would have to uninstall before installing a newer build. To sign every build with one permanent key:

```bash
keytool -genkeypair -keystore starlocator-release.keystore -storetype PKCS12 \
        -alias starlocator -keyalg RSA -keysize 4096 -validity 10000
# use the same password for the keystore and the key
base64 -w0 starlocator-release.keystore     # macOS: base64 -i starlocator-release.keystore
```

Then, under **Settings → Secrets and variables → Actions → New repository secret**, add:

| Secret | Value |
|---|---|
| `ANDROID_KEYSTORE_BASE64` | the base64 output above |
| `ANDROID_SIGNING_PASSWORD` | the keystore/key password |

The key alias must be `starlocator`. Back up the keystore file and password somewhere safe: GitHub secrets can't be read back.

## Project layout

```
src/StarLocator.Core/       Solver library (net10.0): detection, hash table, identification,
                            attitude, astronomy, position + calibration; embedded BSC5 catalog
src/StarLocator.Android/    Android app (net10.0-android): Camera2 manual capture, accelerometer
                            sampling, UI, result rendering, calibration storage, frame export
tests/StarLocator.Tests/    Sky simulator, unit + end-to-end tests, robustness sweep, frame replay
.github/workflows/          CI: test, build, verify and release the APK
```

## Credits

- Star-tracker algorithm: Marbel, Ben-Moshe & Yozevitch, *Sensors* 2020, 20(4), 1106. [doi:10.3390/s20041106](https://doi.org/10.3390/s20041106)
- Star catalog: Yale Bright Star Catalog, 5th revised ed. (Hoffleit & Warren), from the JSON edition at [brettonw/YaleBrightStarCatalog](https://github.com/brettonw/YaleBrightStarCatalog).
- Astronomical formulas: J. Meeus, *Astronomical Algorithms* (GMST, precession). The test suite checks against its worked examples.

Licensed under the [MIT License](LICENSE).
