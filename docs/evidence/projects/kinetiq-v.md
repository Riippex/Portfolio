# Evidence Record: Kinetiq V

- Identifier: `evidence-project-kinetiq-v`
- Slug: `kinetiq-v`
- Kind: Project
- Status: `verified`
- Version: `2026.11.1`
- Last reviewed: 2026-11-05
- Canonical Source: https://github.com/Riippex/kinetiq-v/tree/59f28549049c4dacbf1f713df58757a37489ef3b

## Pipeline

The separate OpenCV 5 computer vision engine implements session-specific person tracking and temporal exercise analysis behind versioned REST contracts.

The engine uses incoming frames to associate movement with a selected workout target and emits exercise observations. The repository includes synthetic evaluation and tooling for a separate held-out evaluation.

[Vision engine and evaluation status](https://github.com/Riippex/kinetiq-v-vision/blob/42d9bb77e7352bfa367bff7c7fb45ef14a0c2b87/README.md)

## Workout platform

A Django GraphQL backend owns routines and workout session state and integrates with the separate vision service.

The product coordinates workout state while the vision service handles camera frame analysis. The product repository describes the boundaries between its backend, phone, and TV clients.

[Product repository architecture](https://github.com/Riippex/kinetiq-v/blob/59f28549049c4dacbf1f713df58757a37489ef3b/README.md)

## Target enrollment

The phone asks the athlete to select and confirm a detected person before their movement can affect the workout session.

The product backend checks session ownership, sends the enrollment frame to the vision service, and returns candidate boxes for explicit confirmation. The selection is scoped to that workout session.

[Target enrollment flow](https://github.com/Riippex/kinetiq-v/blob/59f28549049c4dacbf1f713df58757a37489ef3b/docs/vision-enrollment.md)
