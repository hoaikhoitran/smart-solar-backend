# Smart Solar Backend

Backend service for **Smart Solar Installation, Monitoring and Maintenance Platform** — a Software Engineering Capstone Project developed to digitalize the lifecycle of solar installation projects, from preliminary assessment and consultation to quotation, construction, warranty, and maintenance.

> 🚧 **Status:** In Development  
> 🎓 **Project Type:** FPT University Software Engineering Capstone Project  

---

## Overview

Smart Solar is a platform designed for solar installation service providers to manage the complete lifecycle of solar projects through a centralized digital system.

The platform supports customers from the first consultation and preliminary roof assessment, through quotation and installation, to post-installation warranty and maintenance.

Customers can provide preliminary information about their installation location before an on-site survey. Sales staff can then review this information, perform an initial feasibility assessment, provide reference pricing, and schedule technicians for an official survey.

---

## Problem

Solar installation companies may still manage consultation, quotation, installation, warranty, and maintenance through disconnected or manual processes.

This can lead to:

- Fragmented customer and project information
- Difficulty tracking project progress
- Repeated manual consultation work
- Limited visibility into warranty and maintenance history
- Difficulty coordinating customers, sales staff, and technicians
- Lack of a centralized product and service catalog
- Limited tools for customers to track their projects and service requests

---

## Solution

Smart Solar provides a centralized platform for managing solar installation projects and related services.

Main capabilities include:

- Customer authentication and account management
- Solar product and service catalog
- Preliminary roof assessment
- Reference price estimation
- Sales consultation and feasibility assessment
- On-site survey scheduling
- Official quotation and contract management
- Installation progress tracking
- Warranty management
- Periodic and on-demand maintenance
- Repair and maintenance history
- Management dashboards and reports
- AI-assisted customer support
- Solar panel image analysis

---

## Main Actors

The system uses Role-Based Access Control with five main actor groups.

| Role | Main Responsibilities |
|---|---|
| **Customer** | Perform pre-surveys, request consultation, view quotations, track projects, submit warranty and maintenance requests |
| **Sales** | Review consultation requests, assess feasibility, recommend solutions, prepare quotations |
| **Technician** | Perform on-site surveys, installation, warranty, repair and maintenance |
| **Manager** | Monitor projects, operations and reports |
| **Admin** | Manage users, roles, catalogs and system configuration |

---

# Core Business Modules

## 1. Identity & Access

Responsible for authentication and account security.

Current capabilities include:

- User registration
- Email verification
- Login
- JWT access tokens
- Refresh token rotation
- Logout
- Forgot password
- Reset password
- Change password
- Role-Based Access Control
- Authentication rate limiting

System roles:

```text
CUSTOMER
SALES
TECHNICIAN
MANAGER
ADMIN
```

---

## 2. Product Catalog

Provides the product information required by consultation and pre-survey flows.

Current capabilities include:

- Product management
- Product search
- Product filtering
- Product sorting
- Pagination
- Product detail retrieval
- Product status management
- Soft deletion
- Redis caching
- Catalog-specific rate limiting

Product management operations are restricted to authorized roles such as:

```text
ADMIN
MANAGER
```

The catalog is also designed to provide solar panel specifications such as:

- Rated power
- Width
- Height
- Unit price
- Warranty period
- Technical specification
- Product image URL

These values can later be used by the pre-survey and frontend layout engine.

---

## 3. Pre-Survey

The Pre-Survey module allows customers to provide preliminary information about an installation site before an official technician visit.

The flow includes:

1. Customer authentication
2. Selecting a preferred solar panel
3. Selecting or creating a property site
4. Entering roof information
5. Optional layout / visualization
6. Viewing reference pricing
7. Submitting a consultation request
8. Sales review
9. Scheduling an on-site survey

Typical roof information includes:

- Roof area
- Usable area
- Tilt angle
- Orientation / azimuth
- Obstacles
- Property location

---

## 4. Sales Consultation

After a customer submits a pre-survey, Sales staff can:

- Receive consultation requests
- Review customer-provided roof information
- Review project images
- Assess preliminary installation feasibility
- Review roof accessibility
- Review factors affecting future maintenance
- Provide a reference price range
- Recommend suitable products or service packages
- Schedule an on-site survey

---

## 5. Quotation & Contract

The planned quotation lifecycle includes:

### Preliminary Quotation

Provides an estimated price range based on:

- Pre-survey information
- Product catalog
- Service catalog

### Official Quotation

Created after the technician completes the on-site survey.

The official quotation may consider:

- Actual roof conditions
- Installation location
- Obstacles
- Construction plan
- Additional customer requirements

### Contract

After quotation acceptance:

- Generate contract information
- Record contract value
- Record deposit amount
- Transition the project into construction after deposit confirmation

---

## 6. On-Site Survey & Installation

Technicians perform an on-site survey to verify information submitted during the preliminary assessment.

The survey may include:

- Installation area verification
- Roof shape
- Roof slope
- Access conditions
- Shading obstacles
- Maintenance accessibility
- Installation planning

After confirmation, the project continues to construction and handover.

---

## 7. Warranty & Maintenance

### Warranty

Customers can submit warranty requests for installed solar systems.

The workflow includes:

- Warranty request submission
- Warranty scope verification
- Technician assignment
- Issue handling
- Result updates
- Warranty history

Warranty history can include:

- Execution date
- Assigned technician
- Handling details
- Result
- Before/after images

### Maintenance

The platform supports:

- Periodic maintenance
- On-demand maintenance
- Maintenance tickets
- Technician scheduling
- Maintenance quotation
- Maintenance payment
- Maintenance history

---

## 8. AI Chatbot

A planned RAG-based chatbot will support customer questions related to:

- Solar panels
- Installation processes
- Warranty policies
- Project-related knowledge

The chatbot will use trusted internal documents to reduce unsupported or inaccurate answers.

---

## 9. Solar Panel Image Analysis

A planned Computer Vision module will support image-based solar panel inspection.

Potential use cases include:

- Visible defect detection
- Surface issue analysis
- Maintenance risk assistance
- Supporting warranty and maintenance evaluation

---

# Current Backend Status

## Implemented

- [x] ASP.NET Core 8 Web API foundation
- [x] Modular backend structure
- [x] PostgreSQL persistence
- [x] Entity Framework Core
- [x] JWT authentication
- [x] Email verification
- [x] Refresh token rotation
- [x] Password recovery
- [x] Role-based authorization
- [x] Authentication rate limiting
- [x] RabbitMQ messaging
- [x] Email delivery pipeline
- [x] Redis distributed caching
- [x] Product Catalog module
- [x] Product search / filter / sort
- [x] Product pagination
- [x] Catalog rate limiting
- [x] Catalog cache-aside strategy
- [x] Pre-Survey core database model
- [x] Swagger / OpenAPI
- [x] Automated tests
- [x] Docker support

## In Progress

- [ ] Customer business profile workflow
- [ ] Property Site APIs
- [ ] Pre-Survey CRUD
- [ ] Product selection in Pre-Survey
- [ ] Reference price calculation
- [ ] Sales consultation workflow
- [ ] On-site survey scheduling

## Planned

- [ ] Official quotation
- [ ] Contract management
- [ ] Installation workflow
- [ ] Acceptance and handover
- [ ] Warranty workflow
- [ ] Maintenance workflow
- [ ] Repair workflow
- [ ] Management dashboard
- [ ] Reports
- [ ] RAG chatbot
- [ ] Computer Vision analysis

---

# Tech Stack

| Area | Technology |
|---|---|
| Backend | ASP.NET Core 8 |
| Language | C# |
| ORM | Entity Framework Core 8 |
| Database | PostgreSQL |
| Database Hosting | Supabase |
| Authentication | JWT |
| Authorization | Role-Based Access Control |
| Validation | FluentValidation |
| Cache | Redis |
| Messaging | RabbitMQ |
| Message Bus | MassTransit |
| API Documentation | Swagger / OpenAPI |
| Testing | xUnit |
| Containerization | Docker / Docker Compose |

---

# Architecture

The backend follows a **Modular Monolith** architecture.

```text
SmartSolar.Api
     |
     +----------> SmartSolar.Modules
     |
     +----------> SmartSolar.Infrastructure
                        |
                        +----------> SmartSolar.Modules
```

## Dependency Rules

```text
SmartSolar.Api
    -> SmartSolar.Modules
    -> SmartSolar.Infrastructure

SmartSolar.Infrastructure
    -> SmartSolar.Modules

SmartSolar.Modules
    -X-> SmartSolar.Infrastructure
```

### SmartSolar.Api

Responsible for:

- Controllers
- HTTP request / response models
- Middleware
- Authentication pipeline
- Authorization
- Rate limiting
- Swagger
- Application composition

### SmartSolar.Modules

Contains business modules and application logic.

Examples:

```text
Identity
Catalog
PreSurvey
Common
```

The Modules project does not depend on Infrastructure.

### SmartSolar.Infrastructure

Contains technical implementations such as:

- Entity Framework Core
- PostgreSQL
- Redis
- RabbitMQ
- Email delivery
- JWT infrastructure
- Persistence implementations

---

# Project Structure

```text
smart-solar-backend/
│
├── src/
│   │
│   ├── SmartSolar.Api/
│   │   ├── Controllers/
│   │   ├── Contracts/
│   │   ├── Extensions/
│   │   └── Middlewares/
│   │
│   ├── SmartSolar.Modules/
│   │   ├── Identity/
│   │   ├── Catalog/
│   │   ├── PreSurvey/
│   │   └── Common/
│   │
│   └── SmartSolar.Infrastructure/
│       ├── Persistence/
│       ├── Caching/
│       ├── Messaging/
│       ├── Email/
│       └── Security/
│
├── tests/
│   └── SmartSolar.Tests/
│
├── Dockerfile
├── docker-compose.yml
├── docker-compose.pull.yml
└── README.md
```

---

# Authentication & Authorization

Authentication uses JWT access tokens.

JWT tokens contain:

```text
sub
email
jti
role
```

Example authorization:

```csharp
[Authorize]
```

or:

```csharp
[Authorize(Roles = $"{RoleCodes.Admin},{RoleCodes.Manager}")]
```

This allows APIs to enforce role-based access.

---

# API Response Format

All APIs follow a consistent response envelope.

## Success

```json
{
  "isSuccess": true,
  "traceId": "request-trace-id",
  "data": {},
  "error": null
}
```

## Error

```json
{
  "isSuccess": false,
  "traceId": "request-trace-id",
  "data": null,
  "error": {
    "code": "VALIDATION_FAILED",
    "message": "One or more fields are invalid."
  }
}
```

---

# Pagination

List APIs use server-side pagination.

Example:

```http
GET /api/products?page=1&pageSize=20
```

Example response data:

```json
{
  "items": [],
  "page": 1,
  "pageSize": 20,
  "totalItems": 100,
  "totalPages": 5
}
```

Catalog APIs also support filtering and safe server-side sorting.

---

# Redis Caching

Redis is currently used as a distributed cache.

Architecture:

```text
Application Handler
        |
        v
   ICacheStore
        |
        v
RedisCacheStore
        |
        v
IDistributedCache
        |
        v
      Redis
```

Catalog product detail uses a cache-aside strategy.

Example key:

```text
catalog:product:{productId}
```

Flow:

```text
Request
   |
   v
Redis
 |   |
HIT MISS
 |   |
 |   +------> PostgreSQL
 |               |
 |               v
 |            Set Cache
 |               |
 +---------------+
         |
         v
      Response
```

PostgreSQL remains the source of truth.

---

# RabbitMQ Messaging

RabbitMQ is used for asynchronous operations.

Current use cases include:

- Email verification
- Password reset email delivery

MassTransit is used as the messaging abstraction.

---

# Rate Limiting

The API uses ASP.NET Core Rate Limiting.

Examples include:

- Registration protection
- Login protection
- Email verification protection
- Password recovery protection
- Catalog read rate limiting
- Catalog write rate limiting

Authenticated Catalog limits are partitioned by user identity where possible.

---

# Database

The project uses PostgreSQL through Entity Framework Core.

Database schema changes are managed through incremental migrations.

Existing migrations must never be edited after they have been applied.

Create a migration:

```bash
dotnet ef migrations add <MigrationName> \
  --project src/SmartSolar.Infrastructure \
  --startup-project src/SmartSolar.Api
```

Apply migrations:

```bash
dotnet ef database update \
  --project src/SmartSolar.Infrastructure \
  --startup-project src/SmartSolar.Api
```

---

# Local Development

## Requirements

Install:

- .NET 8 SDK
- Docker
- Git

A valid `.env` file is also required.

---

## Start Infrastructure

Start Redis and RabbitMQ:

```bash
docker compose up -d redis rabbitmq
```

Check containers:

```bash
docker compose ps
```

Redis health test:

```bash
docker compose exec redis redis-cli ping
```

Expected:

```text
PONG
```

---

## Load Environment Variables

On macOS / zsh:

```bash
set -a; source .env; set +a
```

---

## Run API

```bash
dotnet run --project src/SmartSolar.Api --launch-profile https
```

Swagger is available in Development mode.

Example:

```text
https://localhost:7074/swagger
```

The exact port may depend on the launch profile.

---

# Docker

## Build and Run Locally

```bash
docker compose up -d
```

## Run Published API Image

```bash
docker compose -f docker-compose.pull.yml up -d --pull always
```

The pull configuration starts the required infrastructure and pulls the latest published API image.

---

# Testing

Build the solution:

```bash
dotnet build
```

Run all tests:

```bash
dotnet test
```

The project includes unit and integration-oriented tests for implemented backend functionality.

---

# Non-Functional Goals

The project targets the following quality requirements:

### Performance

- Common business API operations should respond within approximately 2 seconds under normal load.
- RAG chatbot responses should target approximately 5 seconds.

### Reliability

- Maintain data consistency across the project lifecycle.
- Prevent invalid business state transitions.
- Avoid losing transactional data.

### Security

- JWT authentication
- Role-Based Access Control
- Secure password hashing
- Rate limiting
- Protected sensitive operations
- Controlled access to project-related resources

### Maintainability

- Modular business-domain architecture
- Clear separation between application and infrastructure code
- OpenAPI documentation
- Automated testing
- Incremental database migrations

---

# Research Component

The project also includes research into a custom RAG pipeline for project-specific document question answering.

The research evaluates:

- Accuracy
- Context relevance
- Hallucination reduction
- Source traceability
- Response time
- Cost

The pipeline may include:

```text
Documents
    |
    v
Preprocessing
    |
    v
Chunking
    |
    v
Embeddings
    |
    v
Vector Store
    |
    v
Semantic Retrieval
    |
    v
LLM Generation
```

---

# Project Roadmap

```text
Identity & Authentication
           |
           v
        Catalog
           |
           v
       Pre-Survey
           |
           v
  Sales Consultation
           |
           v
    On-Site Survey
           |
           v
 Quotation & Contract
           |
           v
     Installation
           |
           v
 Warranty & Maintenance
           |
           v
 Dashboard & Reporting
```

---

# Capstone Work Packages

The project is organized into three major task packages.

### Package 1 — Pre-Survey, Catalog & Sales

Includes:

- Pre-survey
- Catalog
- Customer management
- Sales consultation
- Quotation
- Contract
- RBAC authorization

### Package 2 — Operations & Monitoring

Includes:

- On-site survey
- Construction
- Solar panel image inspection
- Warranty
- Maintenance
- Abnormal condition handling
- Operational tracking

### Package 3 — Testing, Deployment & Documentation

Includes:

- Unit Testing
- Integration Testing
- System Testing
- User Acceptance Testing
- Deployment
- Technical documentation

---

# Expected Deliverables

The complete Smart Solar project is expected to provide:

- Complete backend Web API
- Customer web interface
- Sales interface
- Technician interface
- Manager interface
- Admin interface
- AI Chatbot module
- Solar panel image analysis module
- Technical documentation
- Deployment and installation guide
- Testing documentation

---

# Documentation

Planned project documentation includes:

- User Requirement Specification
- Software Requirement Specification
- Architecture Design Document
- Detailed Design Document
- System Implementation Document
- Testing Document
- Deployment & Installation Guide
- User Guide

---

# Team

**FPT University — Software Engineering Capstone Project**

**Supervisor:** Nguyễn Minh Sang

**Project Duration:** September 2026 – March 2027

---

## License

This repository is developed for academic and capstone project purposes.
