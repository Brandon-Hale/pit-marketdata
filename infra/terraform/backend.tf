terraform {
  backend "s3" {
    bucket       = "pit-marketdata-tfstate-bzun6w"
    key          = "layer1/terraform.tfstate"
    region       = "ap-southeast-2"
    encrypt      = true
    use_lockfile = true
  }
}
