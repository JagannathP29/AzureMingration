Replace the below key's values in the appsettings.json file
        
        Organization 
        Project
        PersonalAccessToken
        CsvFilePath     --File Name and Location to insert Tickets
        AttachmentPath  --Folder Name and Location to attach files based on TicketID

Important:
    
        The actual CSV file's columns "Comment" to be changed to "Comment1" to "CommentN".
        In all the column name - there should not be any space in a single column name.


-------------------------------------------
MAPPING from PivotalTracker to Azure DevOps
-------------------------------------------

    Pivotal Tracker   ||    Azure DevOps
    ------------------------------------
    Epic              ->    Feature
    Feature           ->    User Story
    Bug               ->    User Story
    Chore             ->    User Story
    Release           ->    Release

                            Low - 4
                            Medium - 3
                            High - 2
                            Critical - 2

    Started           =>    Active
    Unstarted         =>    New
    Unscheduled       =>    New
    Delivered         =>    Resolved
    Accepted          =>    Closed
