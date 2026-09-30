# Agentic Hotel Booking

A sample hotel-booking application for browsing hotels, checking room availability, and creating reservations.

## Hotel booking features

- Browse hotels with location and property descriptions.
- View room names, guest capacity, and nightly rates in euros.
- Search room availability by hotel, check-in date, check-out date, and party size.
- Exclude rooms that are too small or overlap an existing reservation.
- Select an available room and create a reservation using the guest name.
- Receive a stable booking reference after confirmation.
- Prevent concurrent overlapping reservations with an atomic database transaction.
- Show clear validation and availability errors in the booking interface.

## Booking rules

- Check-in cannot be in the past.
- Check-out must be after check-in.
- A reservation must include at least one guest and a guest name.
- The selected room must accommodate the complete party.
- A room cannot be booked when any part of the requested stay overlaps an existing reservation.

## Sample catalog

| Hotel | City | Rooms |
|---|---|---|
| Canal House | Amsterdam | Canal King, Family Loft |
| Harbor Light | Rotterdam | Harbor Studio, Panorama Suite |

The catalog is seeded for demonstration. Payments, cancellations, loyalty, dynamic pricing, third-party inventory, and customer accounts are outside the current scope.

## Application

- **Web application:** <https://ambitious-bay-0daea7d03.2.azurestaticapps.net>
- **API health:** <https://ahb-dev-bj5rmi3w3ntgq-api.azurewebsites.net/health>

## Development Pipeline

For a simple, nontechnical walkthrough, start with the [5-10 minute team demo](demo/TEAM-DEMO-GUIDE.md).

For the agentic development process, AI operations, architecture, CI/CD, security controls, observability, local setup, and the end-to-end process diagram, see the [Development Pipeline documentation](docs/development-pipeline/).
