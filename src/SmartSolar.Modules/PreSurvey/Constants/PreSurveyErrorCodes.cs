namespace SmartSolar.Modules.PreSurvey.Constants;

public static class PreSurveyErrorCodes
{
    public const string Unauthorized = "PRESURVEY_UNAUTHORIZED";
    public const string ValidationFailed = "PRESURVEY_VALIDATION_FAILED";
    public const string CustomerAlreadyExists = "CUSTOMER_ALREADY_EXISTS";
    public const string CustomerProfileNotFound = "CUSTOMER_PROFILE_NOT_FOUND";
    public const string PropertySiteNotFound =
    "PROPERTY_SITE_NOT_FOUND";
    public const string PropertySiteNotOwned =
    "PROPERTY_SITE_NOT_OWNED";  
    public const string PreSurveyNotFound =
    "PRE_SURVEY_NOT_FOUND";

    public const string PreSurveyNotOwned =
    "PRE_SURVEY_NOT_OWNED";

    public const string PreSurveyNotEditable =
    "PRE_SURVEY_NOT_EDITABLE";
    public const string PreSurveyAlreadySubmitted =
    "PRE_SURVEY_ALREADY_SUBMITTED";

    public const string PreSurveyIncomplete =
    "PRE_SURVEY_INCOMPLETE";
    public const string SurveyRequestUnavailable =
    "SURVEY_REQUEST_UNAVAILABLE";

    public const string SurveyRequestNotFound =
    "SURVEY_REQUEST_NOT_FOUND";

    public const string SurveyRequestNotAssigned =
    "SURVEY_REQUEST_NOT_ASSIGNED";

    /// <summary>Another write changed the pre-survey since it was read; reload and retry.</summary>
    public const string PreSurveyConcurrentlyModified =
    "PRE_SURVEY_CONCURRENTLY_MODIFIED";
}