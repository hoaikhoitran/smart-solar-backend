using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartSolar.Infrastructure.Persistence.Migrations
{
    /// <summary>Keeps API builds older than DirectBookingAndInstallationSchedule able to submit pre-surveys (see Up).</summary>
    public partial class SurveyRequestSiteCompatibility : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Rolling-deploy compatibility: an API build older than DirectBookingAndInstallationSchedule inserts survey requests without
            // property_site_id. Fill it from the request's own pre-survey (exactly what the current build writes) so those inserts keep
            // working until every instance runs the current build. The current build always sets the column, so the trigger never
            // changes its rows; a direct booking (no pre-survey) is never touched. No existing row is changed.
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION survey_request_fill_property_site() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF NEW.property_site_id IS NULL AND NEW.pre_survey_id IS NOT NULL THEN
                        SELECT p.property_id INTO NEW.property_site_id FROM pre_survey p WHERE p.id = NEW.pre_survey_id;
                    END IF;
                    RETURN NEW;
                END $$;
                DROP TRIGGER IF EXISTS trg_survey_request_fill_property_site ON survey_request;
                CREATE TRIGGER trg_survey_request_fill_property_site BEFORE INSERT ON survey_request
                    FOR EACH ROW EXECUTE FUNCTION survey_request_fill_property_site();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS trg_survey_request_fill_property_site ON survey_request;
                DROP FUNCTION IF EXISTS survey_request_fill_property_site();
                """);
        }
    }
}
