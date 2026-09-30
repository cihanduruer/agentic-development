using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgenticHotelBooking.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddVehiclePreference : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "VehiclePreference",
                table: "Reservations",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "VehiclePreference",
                table: "Reservations");
        }
    }
}
