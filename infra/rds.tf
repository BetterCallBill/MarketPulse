resource "aws_db_subnet_group" "main" {
  name       = "marketpulse"
  subnet_ids = aws_subnet.private[*].id
}

# Express edition: license-included-free, free-tier eligible on db.t3.micro.
# Its 10 GB per-database cap is far above this dataset (ADR-011). Single-AZ,
# no backups, no deletion protection — this stack is built to be torn down.
resource "aws_db_instance" "main" {
  identifier     = "marketpulse"
  engine         = "sqlserver-ex"
  instance_class = "db.t3.micro"
  license_model  = "license-included"

  allocated_storage = 20
  storage_type      = "gp3"

  username                    = var.db_username
  manage_master_user_password = true

  db_subnet_group_name   = aws_db_subnet_group.main.name
  vpc_security_group_ids = [aws_security_group.rds.id]
  publicly_accessible    = false
  multi_az               = false

  backup_retention_period = 0
  skip_final_snapshot     = true
  deletion_protection     = false
  apply_immediately       = true
}
