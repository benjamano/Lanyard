# Copilot Instructions for LanyardApp

## General Coding Style
- Use explicit types for all variable declarations (avoid `var` unless required for anonymous types).
- Prefer efficient, readable, and maintainable code.
- Follow .NET and C# conventions, but prioritize existing project patterns.
- Use modern C# features when supported by the target framework.
- Use dependency injection for services and avoid static state.
- Use async/await for asynchronous operations and always propagate `CancellationToken` where appropriate.
- Use null checks and guard clauses early in methods.
- Use precise exception types and avoid catching base `Exception`.
- Do not swallow exceptions; log and rethrow or let them bubble up.
- Use explicit access modifiers; default to `private` unless a broader scope is required.
- Use PascalCase for class, method, and property names; camelCase for local variables and parameters.
- Use meaningful, descriptive names for all identifiers.
- Avoid magic numbers and strings; use constants or configuration where appropriate.
- Keep methods short and focused on a single responsibility.
- Add comments to explain why something is done, not what is done.
- Do not add unused methods, parameters, or abstractions.
- Reuse existing code and methods whenever possible.

## Blazor-Specific Guidance
- Use explicit types in all Razor and code-behind files.
- Use `@inject` for dependency injection in Razor components.
- Use `[Parameter]` for all component parameters and initialize with explicit types.
- Use `StateHasChanged()` only when necessary to trigger UI updates.
- Prefer `Task` over `void` for async methods in components.
- Use `Dispose` or `IAsyncDisposable` for cleanup in components with long-running tasks or subscriptions.

## Error Handling
- Use `ArgumentNullException.ThrowIfNull(x)` for null checks.
- Use `string.IsNullOrWhiteSpace(x)` for string validation.
- Use `TaskCanceledException` for cancellation scenarios.
- Do not catch and ignore exceptions; always log or handle appropriately.
- Always use `Result<T>` for method and API responses to standardize error and success handling.
    - For successful responses, use `Result<T>.Ok(data)` where `data` is the response data and `T` is its type.
    - For error responses, use `Result<T>.Error(message)` where `message` is a descriptive error string.

## Testing
- Place all tests in the `Lanyard.Tests` project.
- Use MSTest for unit and integration tests.
- Name test classes and methods to reflect the behavior being tested.
- Use Arrange-Act-Assert pattern in all tests.
- Avoid static fields and ensure tests are independent and parallelizable.
- Add tests for all new methods and features. No new method should be added without corresponding tests.
- For UI tests, name the file `UITests` (not `UiTests`).

## Solution Architecture

LanyardApp is a .NET 10 Blazor Server application for managing digital signage/kiosk displays with projection programs, music playback, and client management. The solution uses a multi-project architecture with clear separation of concerns.

### Projects

#### Lanyard.App (Main Blazor Server Application)
- **Purpose**: Main web application hosting the Blazor Server UI
- **Technology**: Blazor Server with Interactive Server rendering
- **Key Features**:
  - Staff portal with ASP.NET Identity authentication
  - Manager features (user management, roles, projection programs)
  - Kiosk display components for rendering projection programs
  - Music player UI and playlist management
  - Rota/scheduling system
  - Clock-in functionality
- **Key Components**:
  - `Components/Kiosk/KioskDisplay.razor` - Renders projection programs on kiosk displays
  - `Components/Manager/ProjectionPrograms/` - Program management UI
  - `Components/Pages/Staff/` - Staff portal pages
  - `Components/Music/` - Music player and playlist components
  - `Services/DragStateService.cs` - Shared state for drag-and-drop operations
- **Place new UI components** in the appropriate `Components` subfolder

#### Lanyard.API
- **Purpose**: Web API controllers for external/AJAX operations
- **Technology**: ASP.NET Core Web API
- **Controllers**:
  - `AuthController.cs` - Authentication endpoints
  - `MusicController.cs` - Music playback API
- **Pattern**: Uses `[ApiController]`, `[Route("api/[controller]")]`, async/await, DI, and returns IActionResult or Result<T> mapped to HTTP responses. Thin controllers delegate to services or DbContext.
- **Place new API controllers** in the `Controllers` folder

#### Lanyard.Client (WPF Kiosk Application)
- **Purpose**: Windows desktop client application for kiosk devices
- **Technology**: .NET 10 WPF with SignalR client connectivity
- **Key Features**:
  - SignalR client connecting to the main Blazor app for real-time updates
  - Local music player (plays audio files on kiosk device)
  - Projection program renderer (displays content on kiosk screens)
  - Packet sniffing functionality (detects game state for interactive displays)
  - WPF popup windows for projection displays
- **Controllers**:
  - `MusicController.cs` - Handles music playback commands from server
  - `ProjectionProgramController.cs` - Renders projection programs locally
- **Services**:
  - `SignalRClient.cs` - Manages SignalR connection to server
  - `MusicPlayer.cs` - Local audio playback
  - `ProjectionProgramsService.cs` - Projection rendering logic
  - `PacketSniffer.cs` - Network packet analysis for game integration
- **Place new client-side logic** in appropriate subfolder (`Controllers`, `Players`, `SignalR`, etc.)

#### Lanyard.Application (Business Logic Layer)
- **Purpose**: Service layer containing all business logic and orchestration
- **Pattern**: Dependency injection with interface-based services
- **Services**:
  - `ApplicationRolesService` - Role and permission management
  - `SecurityService` - Authentication and authorization logic
  - `MusicService` / `MusicPlayerService` - Music functionality and playback coordination
  - `PlaylistService` - Playlist CRUD operations
  - `ClientService` - Kiosk client registration and management
  - `ProjectionProgramService` - Projection program CRUD and execution logic
  - `SignalRControlHub` - SignalR hub for real-time communication with kiosk clients
- **All services use `Result<T>` pattern** for standardized success/error responses
- **Place new business logic services** in the `Services` folder with appropriate subfolders
- **Always create an interface** (e.g., `IFileService`) and implementation (e.g., `FileService`)

#### Lanyard.Infrastructure (Data Access Layer)
- **Purpose**: Entity Framework Core data access, database models, and migrations
- **Technology**: EF Core with SQL Server
- **Key Components**:
  - `ApplicationDbContext` - EF Core database context
  - `Models/` - Database entity models:
    - `ApplicationUserModels.cs` - ASP.NET Identity user and role models
    - `ClientModels.cs` - Kiosk client configuration and screen settings
    - `ProjectionProgramModels.cs` - Projection templates, programs, steps, and parameters
    - `MusicPlayerModels.cs` - Music, playlist, and song data
    - `FileManagementModels.cs` - File metadata and folder hierarchy
  - `DTO/` - Data Transfer Objects:
    - `Result<T>` - Standardized response wrapper for all service methods
    - `MusicDTO`, `ClientDTO`, `LoginDTO` - Domain-specific DTOs
  - `Migrations/` - EF Core database migrations
- **Projection System Architecture**:
  - `ProjectionProgramStepTemplate` - Reusable template definitions (e.g., "Show Text", "Play Video")
  - `ProjectionProgramStepTemplateParameter` - Template parameter definitions
  - `ProjectionProgram` - Container for a sequence of projection steps
  - `ProjectionProgramStep` - Instance of a template within a program
  - `ProjectionProgramParameterValue` - Actual parameter values for each step
- **Place new database models** in the `Models` folder
- **Place new DTOs** in the `DTO` folder
- **Always create EF Core migrations** after model changes

#### Lanyard.Shared
- **Purpose**: Shared DTOs and enums for cross-project communication
- **Used by**: Both server-side projects (App/API/Application) and client (WPF)
- **Content**:
  - `DTO/` - Shared data transfer objects (`ClientDTO`, `GameDTO`, `ProjectionProgramDTO`, `AppDTO`)
  - `Enum/` - Shared enumerations (`GameEnums`, `ClientEnums`)
- **Place shared contracts and enums** here when they need to be accessed by both server and client projects

#### Lanyard.Tests
- **Purpose**: MSTest test project for unit, integration, and UI tests
- **Framework**: MSTest with Arrange-Act-Assert pattern
- **Current Tests**:
  - `ClientServiceTests.cs` - Tests for client service logic
  - `FileManagerUITests.cs` - UI tests for file management features
- **Test Organization**:
  - Use folder structure matching the project being tested (e.g., `Services/Files/` for file service tests)
  - Name test classes after the class being tested with `Tests` suffix (e.g., `FileServiceTests`)
  - Name test methods using pattern: `When{Action}Then{ExpectedResult}` (e.g., `WhenUploadingValidFileThenFileIsSaved`)
- **Place all new tests** in appropriate subfolder matching the code structure

## Resource Management
- Move user-facing strings to resource files for localization.
- Use configuration files for environment-specific settings.

## Database Migrations
- To run a migration, use the `add-migration` and `update-database` command line arguments.
- You may use `update-database` automatically if it is a simple add table or add column.
- If you are deleting or editing data, deleting tables, or altering tables, you must get permission from the user before running `update-database`.

## Security
- Never hardcode secrets or sensitive data.
- Validate all user input.
- Use least privilege for all operations.

---
This file is auto-generated to guide Copilot and contributors to follow the conventions and best practices of the LanyardApp solution.
