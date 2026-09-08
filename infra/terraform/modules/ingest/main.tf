data "aws_caller_identity" "current" {}

# Created explicitly rather than letting Lambda create it on first invoke, so retention is
# set from the start. The default is never expire, which is a slow, silent cost.
resource "aws_cloudwatch_log_group" "ingest" {
  name              = "/aws/lambda/${var.project}-ingest"
  retention_in_days = var.log_retention_days
}

data "aws_iam_policy_document" "assume" {
  statement {
    actions = ["sts:AssumeRole"]

    principals {
      type        = "Service"
      identifiers = ["lambda.amazonaws.com"]
    }
  }
}

resource "aws_iam_role" "ingest" {
  name               = "${var.project}-ingest"
  assume_role_policy = data.aws_iam_policy_document.assume.json
}

# Least privilege, and deliberately no s3:DeleteObject: the store is append-only, so the
# function should be structurally incapable of removing anything.
data "aws_iam_policy_document" "ingest" {
  statement {
    sid       = "ReadWriteData"
    actions   = ["s3:PutObject", "s3:GetObject"]
    resources = ["${var.data_bucket_arn}/*"]
  }

  statement {
    sid       = "ListData"
    actions   = ["s3:ListBucket"]
    resources = [var.data_bucket_arn]
  }

  statement {
    sid = "State"

    actions = [
      "dynamodb:GetItem",
      "dynamodb:PutItem",
      "dynamodb:UpdateItem",
      "dynamodb:Query"
    ]

    resources = [var.table_arn]
  }

  # The vendor key is read at runtime rather than passed as an environment variable, where
  # the console and GetFunctionConfiguration would display it.
  statement {
    sid     = "VendorKey"
    actions = ["ssm:GetParameter"]

    resources = [
      "arn:aws:ssm:${var.region}:${data.aws_caller_identity.current.account_id}:parameter${var.api_key_parameter}"
    ]
  }

  statement {
    sid       = "Logs"
    actions   = ["logs:CreateLogStream", "logs:PutLogEvents"]
    resources = ["${aws_cloudwatch_log_group.ingest.arn}:*"]
  }
}

resource "aws_iam_role_policy" "ingest" {
  name   = "${var.project}-ingest"
  role   = aws_iam_role.ingest.id
  policy = data.aws_iam_policy_document.ingest.json
}

resource "aws_lambda_function" "ingest" {
  function_name = "${var.project}-ingest"
  role          = aws_iam_role.ingest.arn

  # The managed .NET runtime hosts the assembly and calls this method by name. It is not a
  # custom runtime, so there is no bootstrap executable.
  runtime = "dotnet10"
  handler = "MarketData.Lambda::MarketData.Lambda.Function::HandleAsync"

  architectures = ["arm64"]
  memory_size   = 512
  timeout       = 300

  filename         = var.lambda_zip_path
  source_code_hash = filebase64sha256(var.lambda_zip_path)

  # See the variable's description: 1 is correct, but a new account's concurrency quota
  # makes any reservation impossible until it is raised.
  reserved_concurrent_executions = var.reserved_concurrency

  environment {
    variables = {
      MARKETDATA_DATA_BUCKET = var.data_bucket
      MARKETDATA_TABLE_NAME  = var.table_name
      MARKETDATA_REGION      = var.region
    }
  }

  depends_on = [aws_cloudwatch_log_group.ingest]
}

resource "aws_cloudwatch_event_rule" "schedule" {
  name                = "${var.project}-ingest-schedule"
  description         = "Weekdays after the US close."
  schedule_expression = var.schedule_expression
}

resource "aws_cloudwatch_event_target" "ingest" {
  rule = aws_cloudwatch_event_rule.schedule.name
  arn  = aws_lambda_function.ingest.arn
}

resource "aws_lambda_permission" "events" {
  statement_id  = "AllowExecutionFromEventBridge"
  action        = "lambda:InvokeFunction"
  function_name = aws_lambda_function.ingest.function_name
  principal     = "events.amazonaws.com"
  source_arn    = aws_cloudwatch_event_rule.schedule.arn
}
