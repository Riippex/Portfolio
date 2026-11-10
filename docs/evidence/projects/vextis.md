# Evidence Record: Vextis

- Identifier: `evidence-project-vextis`
- Slug: `vextis`
- Kind: Project
- Status: `verified`
- Version: `2026.11.1`
- Last reviewed: 2026-11-05
- Canonical Source: https://github.com/Riippex/Vextis-ERP/tree/009f107ad0e11c9b05eb72139e52da54c1deb33f

## Architecture

Vextis separates the Java Spring Boot enterprise core, which owns CRM, inventory, and billing writes, from a Python Gemini agent runtime that acts through bounded tools.

The core enforces business rules and owns the transactional data. The agent runtime plans and invokes tools across that boundary, while the Angular application presents the workflow to operators.

[Repository architecture](https://github.com/Riippex/Vextis-ERP/blob/009f107ad0e11c9b05eb72139e52da54c1deb33f/README.md)

## Purchase order workflow

The documented order-to-cash demo extracts purchase order details from a PDF, checks and reserves stock, and pauses for human approval before issuing an invoice.

The workflow coordinates CRM, inventory, and billing operations. The public submission documents the flow and the repository demo guide identifies the operator steps and approval checkpoint.

[Order-to-cash demo guide](https://github.com/Riippex/Vextis-ERP/blob/009f107ad0e11c9b05eb72139e52da54c1deb33f/docs/DEMO_SCRIPT.md) · [Hackathon submission](https://devpost.com/software/vextis-erp)

## Mission Control

The Angular Mission Control interface shows agent activity and an audit trail for the demonstrated ERP workflow.

The demo shows operational status, approval decisions, and audit records so an operator can follow what the agents did during the purchase order flow.

[Mission Control demo guide](https://github.com/Riippex/Vextis-ERP/blob/009f107ad0e11c9b05eb72139e52da54c1deb33f/docs/DEMO_SCRIPT.md)
