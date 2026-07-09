using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HoaVoting.Api.Migrations
{
    /// <inheritdoc />
    public partial class MultiQuestionProcesses : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ChoicesJson",
                table: "Proposals");

            migrationBuilder.DropColumn(
                name: "VocdoniBundleId",
                table: "Proposals");

            migrationBuilder.DropColumn(
                name: "VocdoniCensusId",
                table: "Proposals");

            migrationBuilder.DropColumn(
                name: "VotingType",
                table: "Proposals");

            migrationBuilder.CreateTable(
                name: "ProposalQuestions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProposalId = table.Column<int>(type: "INTEGER", nullable: false),
                    Order = table.Column<int>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    ChoicesJson = table.Column<string>(type: "TEXT", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    UpstreamId = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProposalQuestions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProposalQuestions_Proposals_ProposalId",
                        column: x => x.ProposalId,
                        principalTable: "Proposals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProposalQuestions_ProposalId",
                table: "ProposalQuestions",
                column: "ProposalId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProposalQuestions");

            migrationBuilder.AddColumn<string>(
                name: "ChoicesJson",
                table: "Proposals",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "VocdoniBundleId",
                table: "Proposals",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "VocdoniCensusId",
                table: "Proposals",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "VotingType",
                table: "Proposals",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }
    }
}
