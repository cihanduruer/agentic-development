using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgenticHotelBooking.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddRoomAmenities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AmenityData",
                table: "Rooms",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.UpdateData(
                table: "Rooms",
                keyColumn: "Id",
                keyValue: new Guid("10af946b-1653-4ef7-b68c-47b436c34eb2"),
                column: "AmenityData",
                value: "[\"Air conditioning\",\"Coffee maker\",\"Free Wi-Fi\"]");

            migrationBuilder.UpdateData(
                table: "Rooms",
                keyColumn: "Id",
                keyValue: new Guid("163e83ce-a115-4b6d-a5c1-f794b2ced41e"),
                column: "AmenityData",
                value: "[\"Air conditioning\",\"Coffee maker\",\"Free Wi-Fi\"]");

            migrationBuilder.UpdateData(
                table: "Rooms",
                keyColumn: "Id",
                keyValue: new Guid("4135eb2b-04a0-4fa8-bb8d-fca4d16f96be"),
                column: "AmenityData",
                value: "[\"Air conditioning\",\"Free Wi-Fi\",\"Kitchenette\"]");

            migrationBuilder.UpdateData(
                table: "Rooms",
                keyColumn: "Id",
                keyValue: new Guid("bf8607bf-832f-4b05-bf7a-9ff0a1113fef"),
                column: "AmenityData",
                value: "[\"Air conditioning\",\"Balcony\",\"Free Wi-Fi\"]");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AmenityData",
                table: "Rooms");
        }
    }
}
