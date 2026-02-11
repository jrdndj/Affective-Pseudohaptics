# Affective Pseudo-Haptics

Mixed Reality hand interaction prototype exploring **texture (rough/smooth)** and **thermal (hot/cold)** pseudo-haptics using visual and procedural audio feedback.

## Overview
- `HandTextureDriver` → contact telemetry + motion effects  
- `HandThermalDriver` → thermal visual shader feedback  
- `HandTextureAudio` / `HandThermalAudio` → procedural friction & thermal sound  
- `SurfaceData` → assign surface type (Hot / Cold / Rough / Smooth / Neutral)

## Configuration
Global tuning parameters are available in `HapticsGlobalData` on the Global Manager gameobject.

## Authors
- Kristian Paolo David
- Tyrone Justin Sta Maria
- Mikkel Dominic Gamboa
- Jordan Aiko Deja
