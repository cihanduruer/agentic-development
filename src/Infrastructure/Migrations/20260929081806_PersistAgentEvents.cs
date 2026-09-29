using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgenticHotelBooking.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PersistAgentEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AgentEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Timestamp = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    CorrelationId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    WorkItemId = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Agent = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Summary = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    Decision = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Outcome = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Confidence = table.Column<double>(type: "float", nullable: true),
                    DurationMilliseconds = table.Column<long>(type: "bigint", nullable: false),
                    KnowledgeRevision = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentEvents", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgentEvents_Timestamp_Id",
                table: "AgentEvents",
                columns: new[] { "Timestamp", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentEvents");
        }
    }
}
