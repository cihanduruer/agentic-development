# Domain rules

## Language

- **Hotel:** property containing bookable rooms.
- **Room:** capacity-constrained unit with a nightly rate.
- **Stay:** half-open interval from check-in (inclusive) to check-out (exclusive).
- **Reservation:** confirmed allocation of one room for a stay and party.
- **Availability:** absence of an overlapping reservation and sufficient room capacity.

## Rules

1. Check-in cannot be in the past.
2. Check-out must be after check-in.
3. A reservation requires at least one guest and a non-empty guest name.
4. Guest count cannot exceed room capacity.
5. Two reservations for the same room overlap when each starts before the other ends.
6. Reservation creation performs availability checking and insertion atomically.
7. A confirmed reservation receives an immutable, non-secret reference.
8. Dates use `DateOnly`; event timestamps use UTC `DateTimeOffset`.
